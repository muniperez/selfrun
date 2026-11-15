using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Worker.Ssh;
using Renci.SshNet;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed record WorkspaceRuntimeTarget(string VmId, long Generation, string Kind, string Address, string HostFingerprint);

public interface IWorkspaceRuntimeTransport
{
    Task<JsonElement> CallAsync(WorkspaceRuntimeTarget target, string operationId, string action, object? arguments, CancellationToken ct);
    Task<JsonElement> VolumeProbeAsync(WorkspaceRuntimeTarget target, string volumeId, CancellationToken ct);
    Task<JsonElement> BootstrapAsync(WorkspaceRuntimeTarget target, object config, object enrollment, CancellationToken ct);
    Task<string> IssueT3SessionAsync(WorkspaceRuntimeTarget target, string operationId, int ttlSeconds, CancellationToken ct);
    Task StageRepositoryCredentialAsync(WorkspaceRuntimeTarget target, JsonElement credential, CancellationToken ct);
    Task<JsonElement?> ReadRepositoryCredentialAsync(WorkspaceRuntimeTarget target, CancellationToken ct) =>
        throw new NotSupportedException("Repository credential readback is required for safe retirement.");
}

public sealed class WorkspaceRuntimeTransport(IOptions<SshDeploymentOptions> options) : IWorkspaceRuntimeTransport
{
    // SSH.NET accepts octal digits as a decimal argument, not POSIX bit values.
    internal const short PrivateDirectoryMode = 700;
    internal const short PrivateFileMode = 600;
    internal const short RepositoryCredentialDirectoryMode = 711;
    internal const short RepositoryCredentialFileMode = 400;

    internal ConnectionInfo Connection(WorkspaceRuntimeTarget target)
    {
        if (!IPAddress.TryParse(target.Address, out var address) || IPAddress.IsLoopback(address) ||
            target.HostFingerprint.Length != 50 || !target.HostFingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
            throw new InvalidOperationException("Verified provider address and generation SSH host identity are required.");
        var configured = options.Value;
        var content = !string.IsNullOrWhiteSpace(configured.PrivateKeyContent)
            ? configured.PrivateKeyContent : File.ReadAllText(configured.PrivateKeyPath);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var key = string.IsNullOrEmpty(configured.PrivateKeyPassphrase)
            ? new PrivateKeyFile(input) : new PrivateKeyFile(input, configured.PrivateKeyPassphrase);
        return new PrivateKeyConnectionInfo(target.Address, configured.Port, "root", key) { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal SshClient Client(WorkspaceRuntimeTarget target)
    {
        var client = new SshClient(Connection(target));
        Pin(client, target);
        return client;
    }

    private static void Pin(BaseClient client, WorkspaceRuntimeTarget target) =>
        client.HostKeyReceived += (_, key) => key.CanTrust = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes("SHA256:" + key.FingerPrintSHA256), Encoding.ASCII.GetBytes(target.HostFingerprint));

    public async Task<JsonElement> CallAsync(WorkspaceRuntimeTarget target, string operationId, string action, object? arguments, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(arguments ?? new { });
        var request = payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        request["vmId"] = JsonSerializer.SerializeToElement(target.VmId);
        request["generation"] = JsonSerializer.SerializeToElement(target.Generation);
        request["operationId"] = JsonSerializer.SerializeToElement(operationId);
        request["action"] = JsonSerializer.SerializeToElement(action);
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        return Parse(await ExecuteAsync(target,
            $"printf '%s' '{encoded}' | base64 -d | python3 -B /opt/minicloud-workspace/runtime.py call", ct));
    }

    public async Task<JsonElement> VolumeProbeAsync(WorkspaceRuntimeTarget target, string volumeId, CancellationToken ct)
    {
        if (!long.TryParse(volumeId, out var id) || id <= 0) throw new InvalidOperationException("Invalid volume identity.");
        return Parse(await ExecuteAsync(target,
            $"python3 -B /opt/minicloud-workspace/bootstrap.py volume-probe --device /dev/disk/by-id/scsi-0HC_Volume_{id} --provider-device-id scsi-0HC_Volume_{id}", ct));
    }

    public async Task<JsonElement> BootstrapAsync(WorkspaceRuntimeTarget target, object config, object enrollment, CancellationToken ct)
    {
        using (var sftp = new SftpClient(Connection(target)))
        {
            Pin(sftp, target);
            await sftp.ConnectAsync(ct);
            foreach (var directory in new[] { "/etc/minicloud-workspace", "/run/minicloud-workspace" })
            {
                if (!sftp.Exists(directory)) sftp.CreateDirectory(directory);
                sftp.ChangePermissions(directory, PrivateDirectoryMode);
            }
            Upload(sftp, "/etc/minicloud-workspace/runtime.json", config);
            Upload(sftp, "/run/minicloud-workspace/enrollment.json", enrollment);
        }
        return Parse(await ExecuteAsync(target, "python3 -B /opt/minicloud-workspace/bootstrap.py bootstrap", ct));
    }

    public async Task StageRepositoryCredentialAsync(WorkspaceRuntimeTarget target, JsonElement credential, CancellationToken ct)
    {
        using var sftp = new SftpClient(Connection(target));
        Pin(sftp, target);
        await sftp.ConnectAsync(ct);
        const string directory = "/run/minicloud-git";
        if (!sftp.Exists(directory)) sftp.CreateDirectory(directory);
        sftp.ChangePermissions(directory, RepositoryCredentialDirectoryMode);
        const string path = directory + "/credential.json";
        Upload(sftp, path, credential);
        var attributes = sftp.GetAttributes(path);
        attributes.UserId = 2000; attributes.GroupId = 2000;
        sftp.SetAttributes(path, attributes);
        sftp.ChangePermissions(path, RepositoryCredentialFileMode);
    }

    public async Task<JsonElement?> ReadRepositoryCredentialAsync(WorkspaceRuntimeTarget target, CancellationToken ct)
    {
        using var sftp = new SftpClient(Connection(target));
        Pin(sftp, target);
        await sftp.ConnectAsync(ct);
        const string path = "/run/minicloud-git/credential.json";
        if (!sftp.Exists(path)) return null;
        using var stream = sftp.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 16384)
                throw new InvalidOperationException("Repository credential exceeds the bounded readback size.");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    public Task<string> IssueT3SessionAsync(WorkspaceRuntimeTarget target, string operationId, int ttlSeconds, CancellationToken ct)
    {
        if (operationId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) ||
            target.VmId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || ttlSeconds is < 60 or > 3600)
            throw new InvalidOperationException("Invalid generation session request.");
        return ExecuteAsync(target,
            $"python3 -B /opt/minicloud-workspace/issue-t3-session.py --vm-id {target.VmId} --generation {WorkspaceBootstrap.Generation(target.Generation)} --operation-id {operationId} --subject minicloud-gateway --ttl {ttlSeconds} --scope terminal:operate --scope orchestration:operate --scope terminal:read --scope orchestration:read", ct);
    }

    private async Task<string> ExecuteAsync(WorkspaceRuntimeTarget target, string commandText, CancellationToken ct)
    {
        using var client = Client(target);
        await client.ConnectAsync(ct);
        using var command = client.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(75);
        await command.ExecuteAsync(ct);
        if (command.Result.Length > 1024 * 1024 || string.IsNullOrWhiteSpace(command.Result))
            throw new InvalidOperationException("Workspace management returned no bounded response.");
        if (command.ExitStatus != 0 && !command.Result.TrimStart().StartsWith('{'))
            throw new InvalidOperationException("Workspace management command failed; credentials and command output were withheld.");
        return command.Result.Trim();
    }

    private static void Upload(SftpClient client, string path, object payload)
    {
        var staged = path + ".staged";
        using var stream = client.Open(staged, FileMode.Create, FileAccess.Write);
        client.ChangePermissions(staged, PrivateFileMode);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(payload));
        stream.Flush(); stream.Close();
        if (client.Exists(path)) client.DeleteFile(path);
        client.RenameFile(staged, path);
    }

    private static JsonElement Parse(string value)
    {
        using var json = JsonDocument.Parse(value);
        return json.RootElement.Clone();
    }
}
