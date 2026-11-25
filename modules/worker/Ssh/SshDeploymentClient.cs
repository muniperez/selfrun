using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace Selfrun.Worker.Ssh;

/// <summary>
/// SSH.NET implementation of <see cref="ISshDeploymentClient"/>.
/// Connects as <c>selfrun@&lt;server-ip&gt;</c>, SFTPs the deployment
/// payload plus remote-deploy.sh, then runs it with stdin closed.
/// </summary>
public sealed class SshDeploymentClient : ISshDeploymentClient
{
    internal const short PrivateDeploymentDirectoryMode = 700;
    internal const short PrivatePayloadFileMode = 600;
    private readonly SshDeploymentOptions _options;
    private readonly ILogger<SshDeploymentClient>? _logger;
    private readonly ISshHostKeyPinSource? _pinSource;

    public SshDeploymentClient(
        IOptions<SshDeploymentOptions> options,
        ILogger<SshDeploymentClient>? logger = null,
        ISshHostKeyPinSource? pinSource = null)
        : this(options.Value, logger, pinSource)
    {
    }

    public SshDeploymentClient(
        SshDeploymentOptions options,
        ILogger<SshDeploymentClient>? logger = null,
        ISshHostKeyPinSource? pinSource = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _pinSource = pinSource;
    }

    public async Task<SshDeploymentResult> ExecuteDeploymentAsync(
        SshDeploymentRequest request,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onLogLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PrivateKeyContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteDeployScriptContent);

        cancellationToken.ThrowIfCancellationRequested();

        var trust = await ResolveTrustAsync(request.Target, cancellationToken);
        request = request with { Target = request.Target with { ExpectedFingerprint = trust.EnrolledFingerprint } };
        try
        {
            return await ExecuteDeploymentCoreAsync(request, trust, onLogLine, cancellationToken);
        }
        catch (Exception ex) when (ex is not SshHostKeyTrustException && trust.RejectedFingerprint is not null)
        {
            throw trust.MismatchException();
        }
    }

    private async Task<SshDeploymentResult> ExecuteDeploymentCoreAsync(
        SshDeploymentRequest request,
        HostKeyTrust trust,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        var remoteDir = request.RemoteDirectory.TrimEnd('/');
        var remotePayloadPath = $"{remoteDir}/payload.json";
        var remoteScriptPath = $"{remoteDir}/remote-deploy.sh";

        using var buildPayload = JsonDocument.Parse(request.PayloadJson);
        if (buildPayload.RootElement.TryGetProperty("build_artifacts", out var buildArtifacts) &&
            buildArtifacts.ValueKind == JsonValueKind.Array && buildArtifacts.GetArrayLength() > 0)
        {
            const string bootstrapWait = """
                bash -c 'set -euo pipefail
                if command -v cloud-init >/dev/null 2>&1; then
                  status=0; timeout 600 sudo -n cloud-init status --wait >/dev/null 2>&1 || status=$?
                  case "$status" in 0|2) ;; *) exit 41 ;; esac
                fi'
                """;
            var bootstrapExit = await ExecuteCommandAsync(request.Target, request.Username,
                request.PrivateKeyContent, bootstrapWait, (_, _) => Task.CompletedTask, cancellationToken);
            if (bootstrapExit != 0)
            {
                throw new InvalidOperationException("buildkit_unavailable: runtime bootstrap or local BuildKit preflight failed before artifact transfer; use the documented existing-server upgrade.");
            }

            // Bootstrap can update account groups after its SSH session opens.
            // ExecuteCommandAsync opens a fresh connection for the Docker checks.
            const string builderPreflight = """
                bash -c 'set -euo pipefail
                docker buildx version >/dev/null 2>&1
                docker buildx inspect --builder "$(docker context show)" | grep -E "^Driver:[[:space:]]+docker$" >/dev/null'
                """;
            var preflightExit = await ExecuteCommandAsync(request.Target, request.Username,
                request.PrivateKeyContent, builderPreflight, (_, _) => Task.CompletedTask, cancellationToken);
            if (preflightExit != 0)
            {
                throw new InvalidOperationException("buildkit_unavailable: runtime bootstrap or local BuildKit preflight failed before artifact transfer; use the documented existing-server upgrade.");
            }
        }

        var transferStartedAt = DateTimeOffset.UtcNow;
        var transferTimer = Stopwatch.StartNew();
        var configuredTransfers = _options.MaxParallelArtifactTransfers;
        var effectiveTransfers = Math.Min(configuredTransfers, request.Artifacts.Count);
        var artifactBytes = request.Artifacts.Sum(item => item.SizeBytes);
        try
        {
            await UploadPayloadAsync(request, trust, remoteDir, remotePayloadPath, remoteScriptPath, cancellationToken);
            transferTimer.Stop();
            await onLogLine("timing",
                $"TIMING phase=sftp_transfer deployment_id={DeploymentIdFromPayload(request.PayloadJson)} " +
                $"started_at={transferStartedAt:O} duration_ms={transferTimer.ElapsedMilliseconds} " +
                $"artifact_bytes={artifactBytes} artifact_count={request.Artifacts.Count} " +
                $"configured_concurrency={configuredTransfers} effective_concurrency={effectiveTransfers} outcome=succeeded");
        }
        catch
        {
            transferTimer.Stop();
            await onLogLine("timing",
                $"TIMING phase=sftp_transfer deployment_id={DeploymentIdFromPayload(request.PayloadJson)} " +
                $"started_at={transferStartedAt:O} duration_ms={transferTimer.ElapsedMilliseconds} " +
                $"artifact_bytes={artifactBytes} artifact_count={request.Artifacts.Count} " +
                $"configured_concurrency={configuredTransfers} effective_concurrency={effectiveTransfers} outcome=failed");
            throw;
        }

        var commandTimeout = request.CommandTimeout > TimeSpan.Zero
            ? request.CommandTimeout
            : _options.CommandTimeout;

        var remoteProcessPath = $"{remoteDir}/process.pid";
        var ownershipToken = GetPayloadString(request.PayloadJson, "ownership_token");
        var leasePath = $"{remoteDir}/worker.lease";
        var remoteCommand =
            $"chmod 700 {EscapeShell(remoteScriptPath)} || exit $?; " +
            $"setsid bash {EscapeShell(remoteScriptPath)} {EscapeShell(remotePayloadPath)} </dev/null & child=$!; " +
            $"printf '%s\\n' \"$child\" > {EscapeShell(remoteProcessPath)}; " +
            $"wait \"$child\"; status=$?; rm -f {EscapeShell(remoteProcessPath)} {EscapeShell(remotePayloadPath)}; exit $status";
        var outputLines = new List<string>();
        var outputGate = new object();
        string? completionSha = null;
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaseConnection = string.IsNullOrWhiteSpace(ownershipToken)
            ? null
            : new WorkerLeaseConnection(() => CreateClient(trust,
                () => new SshClient(BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent))),
                leasePath, ownershipToken!);
        var leaseMonitor = leaseConnection is null ? null : new WorkerLeaseMonitor(leaseConnection.RefreshAsync);
        var leaseTask = Task.CompletedTask;
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = leaseFailure.Task.ContinueWith(_ => commandCts.Cancel(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        int exitCode;
        try
        {
            if (leaseMonitor is not null)
            {
                // Uploads can exceed the runtime's lease lifetime. Confirm
                // ownership and freshness before starting any remote process.
                await leaseMonitor.RefreshBeforeStartAsync(cancellationToken);
                leaseTask = leaseMonitor.RunAsync(leaseFailure, leaseCts.Token);
            }
            exitCode = await RunRemoteCommandAsync(
            trust,
            request.Host,
            request.Username,
            request.PrivateKeyContent,
            remoteCommand,
            commandTimeout,
            async (source, line) =>
            {
                var isCompletionMarker = line.StartsWith("__SELFRUN_DEPLOY_COMPLETE__=", StringComparison.Ordinal);
                lock (outputGate)
                {
                    outputLines.Add(line);
                    if (isCompletionMarker)
                    {
                        completionSha = line["__SELFRUN_DEPLOY_COMPLETE__=".Length..].Trim();
                    }
                }

                if (!isCompletionMarker)
                {
                    await onLogLine(source, line);
                }
            },
            commandCts.Token,
            remoteProcessPath);
        }
        catch (OperationCanceledException) when (leaseFailure.Task.IsCompletedSuccessfully)
        {
            var failure = await leaseFailure.Task;
            _logger?.LogWarning(failure, "Worker ownership lease refresh failed during SSH deployment.");
            throw new RemoteQuiescenceException($"Worker lease refresh failed: {failure.Message}");
        }
        finally
        {
            await StopLeaseAndCleanupAsync(leaseCts, leaseTask, () => Task.Run(() =>
            {
                TryDeleteRemoteFile(request, trust, leasePath);
                TryDeleteRemoteFile(request, trust, leasePath + ".lock");
            }, CancellationToken.None));
        }

        return new SshDeploymentResult(
            exitCode,
            completionSha is not null,
            completionSha,
            outputLines);
    }

    internal static async Task StopLeaseAndCleanupAsync(
        CancellationTokenSource leaseCts, Task leaseTask, Func<Task> cleanup)
    {
        // The remote shell must not remove the lease while its refresher is alive:
        // a final refresh can otherwise cancel a successfully completed rollout.
        await leaseCts.CancelAsync();
        try { await leaseTask; } catch (OperationCanceledException) { }
        await cleanup();
    }

    private static string DeploymentIdFromPayload(string payloadJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty("deployment_id", out var id) ? id.GetString() ?? "unknown" : "unknown";
    }

    public Task<int> ExecuteCommandAsync(
        string host,
        string username,
        string privateKeyContent,
        string command,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken) =>
        ExecuteCommandAsync(new SshTarget(string.Empty, host), username, privateKeyContent,
            command, onLogLine, cancellationToken);

    public async Task<int> ExecuteCommandAsync(
        SshTarget target,
        string username,
        string privateKeyContent,
        string command,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var host = target.Host;
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(onLogLine);

        cancellationToken.ThrowIfCancellationRequested();
        var trust = await ResolveTrustAsync(target, cancellationToken);
        try
        {
            return await RunRemoteCommandAsync(
            trust,
            host,
            username,
            privateKeyContent,
            command,
            _options.CommandTimeout,
            onLogLine,
            cancellationToken,
            remoteProcessPath: null);
        }
        catch (Exception ex) when (ex is not SshHostKeyTrustException && trust.RejectedFingerprint is not null)
        {
            throw trust.MismatchException();
        }
    }

    private async Task UploadPayloadAsync(
        SshDeploymentRequest request,
        HostKeyTrust trust,
        string remoteDir,
        string remotePayloadPath,
        string remoteScriptPath,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var connectionInfo = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var sftp = CreateClient(trust, () => new SftpClient(connectionInfo));
            sftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            sftp.Connect();
            try
            {
                if (!sftp.Exists(remoteDir))
                {
                    sftp.CreateDirectory(remoteDir);
                }
                sftp.ChangePermissions(remoteDir, PrivateDeploymentDirectoryMode); // SSH.NET expects octal digits as a decimal argument.

                UploadText(sftp, remotePayloadPath, request.PayloadJson);
                sftp.ChangePermissions(remotePayloadPath, PrivatePayloadFileMode);
                UploadText(sftp, remoteScriptPath, request.RemoteDeployScriptContent);
                var rolloutGatePath = $"{remoteDir}/rollout.ready";
                var preparationReadyPath = $"{remoteDir}/preparation.ready";
                if (sftp.Exists(rolloutGatePath))
                {
                    sftp.DeleteFile(rolloutGatePath);
                }
                if (sftp.Exists(preparationReadyPath))
                {
                    sftp.DeleteFile(preparationReadyPath);
                }

            }
            finally
            {
                sftp.Disconnect();
            }
        }, cancellationToken);

        var transferLimit = Math.Min(_options.MaxParallelArtifactTransfers, Math.Max(1, request.Artifacts.Count));
        await Parallel.ForEachAsync(
            request.Artifacts,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = transferLimit,
            },
            async (artifact, ct) =>
            {
                var artifactConnection = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
                using var artifactSftp = CreateClient(trust, () => new SftpClient(artifactConnection));
                artifactSftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
                var remoteArtifactPath = $"{remoteDir}/{artifact.RemoteFileName}";
                try
                {
                    // SSH.NET's ConnectAsync pins a small socket send buffer, capping
                    // uploads near 2 MB/s at ~100 ms RTT. Connect() requests a larger one.
                    await Task.Run(artifactSftp.Connect, ct);
                    using var source = await artifact.OpenStream(ct);
                    await artifactSftp.UploadFileAsync(source, remoteArtifactPath, true, null, ct);
                    ct.ThrowIfCancellationRequested();
                    artifactSftp.ChangePermissions(remoteArtifactPath, PrivatePayloadFileMode);
                    _logger?.LogInformation("SFTP uploaded artifact for service {Service} to {RemotePath}", artifact.ServiceName, remoteArtifactPath);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    _ = Task.Run(() => TryDeleteRemoteFile(request, trust, remoteArtifactPath), CancellationToken.None);
                    throw new OperationCanceledException(ct);
                }
                catch
                {
                    TryDeleteRemoteFile(request, trust, remoteArtifactPath);
                    throw;
                }
            });

        await Task.Run(() =>
        {
            var readyConnection = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var readySftp = CreateClient(trust, () => new SftpClient(readyConnection));
            readySftp.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            readySftp.Connect();
            try
            {
                var ownershipToken = GetPayloadString(request.PayloadJson, "ownership_token");
                if (!string.IsNullOrWhiteSpace(ownershipToken))
                {
                    UploadText(readySftp, $"{remoteDir}/worker.lease", ownershipToken);
                    readySftp.ChangePermissions($"{remoteDir}/worker.lease", PrivatePayloadFileMode);
                }
                var readyPath = $"{remoteDir}/preparation.ready";
                UploadText(readySftp, readyPath, ownershipToken + "\n");
                readySftp.ChangePermissions(readyPath, PrivatePayloadFileMode);
            }
            finally
            {
                readySftp.Disconnect();
            }
        }, cancellationToken);
    }

    private ConnectionInfo BuildConnectionInfo(string host, string username, string privateKeyContent)
    {
        var keyBytes = Encoding.UTF8.GetBytes(privateKeyContent);
        var keyStream = new MemoryStream(keyBytes);
        PrivateKeyFile keyFile = string.IsNullOrEmpty(_options.PrivateKeyPassphrase)
            ? new PrivateKeyFile(keyStream)
            : new PrivateKeyFile(keyStream, _options.PrivateKeyPassphrase);
        return new PrivateKeyConnectionInfo(host, _options.Port, username, keyFile)
        {
            Timeout = _options.ConnectionTimeout,
        };
    }

    private async Task<HostKeyTrust> ResolveTrustAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var host = target.Host;
        if (_pinSource is null && !string.IsNullOrWhiteSpace(target.ServerInstanceId))
        {
            throw new SshHostKeyTrustException(SshHostKeyTrustException.UntrustedFailureCode,
                $"Selected server {target.ServerInstanceId}'s registration cannot be verified without a pin source; refusing SSH.");
        }
        var enrolled = _pinSource is null
            ? null
            : await _pinSource.GetPinnedFingerprintAsync(target, cancellationToken);
        if (target.ExpectedFingerprint is not null && enrolled != target.ExpectedFingerprint)
        {
            throw new SshHostKeyTrustException(SshHostKeyTrustException.UntrustedFailureCode,
                $"Selected server {target.ServerInstanceId}'s enrolled SSH host key changed during the operation; refusing SSH.");
        }
        var operatorPins = SshHostPins.Read(_options.HostKeyPinsFile, host);
        IReadOnlySet<string> pins = operatorPins;
        if (enrolled is not null)
        {
            if (operatorPins.Any(pin => pin != enrolled))
            {
                _logger?.LogWarning("Ignoring conflicting host-pins entries for enrolled server {ServerInstanceId} at {Host}.",
                    target.ServerInstanceId, host);
            }
            pins = new HashSet<string>(StringComparer.Ordinal) { enrolled };
        }

        if (pins.Count == 0)
        {
            throw new SshHostKeyTrustException(SshHostKeyTrustException.UntrustedFailureCode,
                $"No trusted SSH host key is configured for {host}. Servers provisioned by this worker are enrolled " +
                "automatically during first boot; a manually added, discovered or pre-upgrade server needs its verified " +
                $"Ed25519 key in the worker host-pins file as '{host} SHA256:<fingerprint>' before retrying.");
        }

        return new HostKeyTrust(host, pins, enrolled);
    }

    private static T CreateClient<T>(HostKeyTrust trust, Func<T> factory) where T : BaseClient
    {
        var client = factory();
        client.HostKeyReceived += (_, key) =>
        {
            var fingerprint = "SHA256:" + key.FingerPrintSHA256;
            key.CanTrust = trust.Pins.Contains(fingerprint);
            if (!key.CanTrust) { trust.RejectedFingerprint = fingerprint; }
        };
        return client;
    }

    private sealed class HostKeyTrust(string host, IReadOnlySet<string> pins, string? enrolledFingerprint)
    {
        private string? _rejected;

        public IReadOnlySet<string> Pins { get; } = pins;
        public string? EnrolledFingerprint { get; } = enrolledFingerprint;

        public string? RejectedFingerprint
        {
            get => Volatile.Read(ref _rejected);
            set => Volatile.Write(ref _rejected, value);
        }

        public SshHostKeyTrustException MismatchException() => new(SshHostKeyTrustException.MismatchFailureCode,
            $"SSH host key {RejectedFingerprint} presented by {host} does not match its trusted pin; refusing to connect. " +
            "Verify the server out of band. If it was legitimately rebuilt, update its pin before retrying.");
    }

    private void TryDeleteRemoteFile(SshDeploymentRequest request, HostKeyTrust trust, string remotePath)
    {
        try
        {
            var connectionInfo = BuildConnectionInfo(request.Host, request.Username, request.PrivateKeyContent);
            using var cleanup = CreateClient(trust, () => new SftpClient(connectionInfo));
            cleanup.ConnectionInfo.Timeout = _options.ConnectionTimeout;
            cleanup.Connect();
            if (cleanup.Exists(remotePath))
            {
                cleanup.DeleteFile(remotePath);
            }
            cleanup.Disconnect();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove partial SFTP artifact {RemotePath}", remotePath);
        }
    }

    private static string? GetPayloadString(string payloadJson, string name)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<int> RunRemoteCommandAsync(
        HostKeyTrust trust,
        string host,
        string username,
        string privateKeyContent,
        string commandText,
        TimeSpan commandTimeout,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken,
        string? remoteProcessPath)
    {
        var connectionInfo = BuildConnectionInfo(host, username, privateKeyContent);

        return await Task.Run(async () =>
        {
            using var client = CreateClient(trust, () => new SshClient(connectionInfo));
            await client.ConnectAsync(cancellationToken);
            try
            {
                using var command = client.CreateCommand(commandText);
                command.CommandTimeout = commandTimeout;

                cancellationToken.ThrowIfCancellationRequested();
                // EndExecute reads command.Result, which drains OutputStream and can
                // steal trailing lines (including the completion marker) from the pump.
                var executeTask = command.ExecuteAsync(CancellationToken.None);

                var stdoutTask = PumpLinesAsync(
                    new StreamReader(command.OutputStream, Encoding.UTF8),
                    "deploy",
                    onLogLine,
                    cancellationToken);
                var stderrTask = PumpLinesAsync(
                    new StreamReader(command.ExtendedOutputStream, Encoding.UTF8),
                    "deploy",
                    onLogLine,
                    cancellationToken);

                using var timeoutCts = new CancellationTokenSource(commandTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                try
                {
                    await Task.WhenAll(executeTask, stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
                }
                catch (Exception) when (linkedCts.IsCancellationRequested)
                {
                    try
                    {
                        command.CancelAsync();
                    }
                    catch
                    {
                        // Best effort cancel on timeout.
                    }

                    if (!TerminateRemoteProcessGroup(client, remoteProcessPath))
                    {
                        throw new RemoteQuiescenceException(
                            "Remote deployment cancellation could not confirm process-group termination.");
                    }

                    if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Remote SSH command timed out after {commandTimeout.TotalMinutes:F1} minutes.");
                    }

                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception) when (remoteProcessPath is not null)
                {
                    if (!TerminateRemoteProcessGroup(client, remoteProcessPath))
                    {
                        throw new RemoteQuiescenceException("SSH execution failed and remote deployment termination could not be confirmed.");
                    }
                    throw;
                }

                return command.ExitStatus ?? -1;
            }
            finally
            {
                client.Disconnect();
            }
        }, cancellationToken);
    }

    private static bool TerminateRemoteProcessGroup(SshClient client, string? remoteProcessPath)
    {
        if (string.IsNullOrWhiteSpace(remoteProcessPath))
        {
            return true;
        }
        try
        {
            var stopCommand =
                $"if test -f {EscapeShell(remoteProcessPath)}; then " +
                $"pid=$(cat {EscapeShell(remoteProcessPath)}); case \"$pid\" in ''|*[!0-9]*) exit 43;; esac; " +
                "kill -TERM -- -$pid 2>/dev/null || true; " +
                "i=0; while kill -0 -- -$pid 2>/dev/null && test $i -lt 120; do sleep 0.1; i=$((i+1)); done; " +
                "kill -KILL -- -$pid 2>/dev/null || true; " +
                "i=0; while kill -0 -- -$pid 2>/dev/null && test $i -lt 20; do sleep 0.1; i=$((i+1)); done; " +
                $"! kill -0 -- -$pid 2>/dev/null; else exit 42; fi";
            using var terminate = client.CreateCommand("bash -c " + EscapeShell(stopCommand));
            terminate.CommandTimeout = TimeSpan.FromSeconds(20);
            return terminate.Execute() == string.Empty && terminate.ExitStatus == 0;
        }
        catch { return false; } // Disconnection is not proof that remote execution stopped.
    }

    private static async Task PumpLinesAsync(
        StreamReader reader,
        string source,
        Func<string, string, Task> onLogLine,
        CancellationToken cancellationToken)
    {
        using (reader)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                await onLogLine(source, line);
            }
        }
    }

    private void UploadText(SftpClient sftp, string remotePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        sftp.UploadFile(stream, remotePath, true);
        _logger?.LogDebug("SFTP uploaded {RemotePath} ({Bytes} bytes)", remotePath, bytes.Length);
    }

    private static string EscapeShell(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }
}
