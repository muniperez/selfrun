using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class ServerHostKeyEnrollmentTests
{
    private const string ServerId = "0123456789abcdef0123456789abcdef";

    internal static byte[] Ed25519Blob(byte[]? publicKey = null)
    {
        var name = Encoding.ASCII.GetBytes("ssh-ed25519");
        var key = publicKey ?? RandomNumberGenerator.GetBytes(32);
        return [0, 0, 0, (byte)name.Length, .. name, 0, 0, 0, 32, .. key];
    }

    // Independent of the product implementation: the documented proof message format.
    internal static string Banner(string serverId, byte[] secret, byte[] blob) =>
        "minicloud-server-host-v1 " + Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(
            $"minicloud-server-host-v1\n{serverId}\nssh-ed25519 {Convert.ToBase64String(blob)}"))).ToLowerInvariant() + "\n";

    [Fact]
    public void VerifyAcceptsOnlyTheProvenEd25519Key()
    {
        var secret = ServerHostKeyEnrollment.CreateSecret();
        var blob = Ed25519Blob();
        var banner = Banner(ServerId, secret, blob);

        var fingerprint = ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, banner));

        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), fingerprint);
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", Ed25519Blob(), banner)));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, ServerHostKeyEnrollment.CreateSecret(), new("ssh-ed25519", blob, banner)));
        Assert.Null(ServerHostKeyEnrollment.Verify("fedcba9876543210fedcba9876543210", secret, new("ssh-ed25519", blob, banner)));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, banner.Replace("v1 ", "v1 0").TrimEnd() + "\n")));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, null)));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, "Welcome\n")));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, banner + banner)));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-rsa", blob, banner)));
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, null));
        // The seed wizard's label must not authorize a provisioned server.
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, banner.Replace("server-host", "seed-host"))));
    }

    [Fact]
    public void CloudInitRunsEnrollmentBeforeTheRestOfFirstBoot()
    {
        var script = ServerHostKeyEnrollment.CloudInitScript(ServerId, ServerHostKeyEnrollment.CreateSecret());
        var cloudInit = ResolveProvisionServerStep.BuildCloudInit("myorg", "myapp", "ssh-ed25519 AAAAKEY", script);

        Assert.Contains("write_files:\n  - path: /run/minicloud/host-enrollment.py\n    owner: root:root\n    permissions: '0700'", cloudInit);
        Assert.Contains("PROTOCOL = \"minicloud-server-host-v1\"", cloudInit);
        Assert.Contains($"SERVER_ID = \"{ServerId}\"", cloudInit);
        Assert.Contains("runcmd:\n  - python3 /run/minicloud/host-enrollment.py; rm -f /run/minicloud/host-enrollment.py\n  - |\n    if [ ! -f /swapfile ]", cloudInit);
        Assert.DoesNotContain("host-enrollment", ResolveProvisionServerStep.BuildCloudInit("myorg", "myapp", "ssh-ed25519 AAAAKEY"));
    }

    [Fact]
    public async Task CloudInitScriptProofMatchesTheWorkerVerifier()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) { return; }
        using (var probe = Process.Start(new ProcessStartInfo("/usr/bin/python3", "-c \"import yaml\"") { RedirectStandardError = true })!)
        {
            await probe.WaitForExitAsync();
            if (probe.ExitCode != 0) { return; }
        }
        var secret = ServerHostKeyEnrollment.CreateSecret();
        var blob = Ed25519Blob();
        var script = ServerHostKeyEnrollment.CloudInitScript(ServerId, secret);
        var cloudInit = ResolveProvisionServerStep.BuildCloudInit("myorg", "myapp", "ssh-ed25519 AAAAKEY", script);
        var directory = CreateFixtureDirectory("host-enrollment-");
        try
        {
            var userData = Path.Combine(directory.FullName, "user-data");
            await File.WriteAllTextAsync(userData, cloudInit);
            // Parse the delivered cloud-config and run the embedded script exactly as written to disk.
            var output = await RunAsync("/usr/bin/python3", "-c", """
                import json, runpy, sys, yaml
                data = yaml.safe_load(open(sys.argv[1]))
                entry = data["write_files"][0]
                path = sys.argv[1] + ".py"
                open(path, "w").write(entry["content"])
                proof = runpy.run_path(path)["proof"](sys.argv[2] + " root@host\n")
                print(json.dumps({"path": entry["path"], "mode": entry["permissions"], "first": data["runcmd"][0], "proof": proof}))
                """, userData, "ssh-ed25519 " + Convert.ToBase64String(blob));
            using var result = JsonDocument.Parse(output);
            Assert.Equal("/run/minicloud/host-enrollment.py", result.RootElement.GetProperty("path").GetString());
            Assert.Equal("0700", result.RootElement.GetProperty("mode").GetString());
            Assert.StartsWith("python3 /run/minicloud/host-enrollment.py", result.RootElement.GetProperty("first").GetString());
            var banner = "minicloud-server-host-v1 " + result.RootElement.GetProperty("proof").GetString() + "\n";
            Assert.Equal(Banner(ServerId, secret, blob), banner);
            Assert.NotNull(ServerHostKeyEnrollment.Verify(ServerId, secret, new("ssh-ed25519", blob, banner)));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SshClientRefusesHostsWithoutAnyPinBeforeConnecting(bool configureFile)
    {
        var directory = CreateFixtureDirectory("host-pins-");
        try
        {
            var pins = Path.Combine(directory.FullName, "host-pins");
            await File.WriteAllTextAsync(pins, "");
            var client = new SshDeploymentClient(new SshDeploymentOptions { HostKeyPinsFile = configureFile ? pins : "" }, null, new FixedPins(null));

            var refused = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
                "192.0.2.10", "minicloud", "unused-key", "true", (_, _) => Task.CompletedTask, CancellationToken.None));

            Assert.Equal("ssh_host_key_untrusted", refused.FailureCode);
            Assert.Contains("enrolled automatically", refused.Message);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [SshdFact]
    public async Task EnrolledPinAuthorizesRealSshdAndMismatchIsRefused()
    {
        await using var sshd = await LocalSshd.StartAsync();
        var secret = ServerHostKeyEnrollment.CreateSecret();
        var proof = await RunAsync("/usr/bin/python3", "-c", """
            import runpy, sys
            print(runpy.run_path(sys.argv[1])["proof"](open(sys.argv[2]).read()))
            """, await sshd.WriteAsync("enroll.py", ServerHostKeyEnrollment.CloudInitScript(ServerId, secret)), sshd.HostPublicKeyPath);
        await sshd.WriteAsync("banner", $"minicloud-server-host-v1 {proof.Trim()}\n");
        await sshd.ListenAsync();

        var observation = await new SshNetServerHostKeyProbe().ObserveAsync("127.0.0.1", sshd.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
        var fingerprint = ServerHostKeyEnrollment.Verify(ServerId, secret, observation);
        Assert.Equal(sshd.Fingerprint, fingerprint);
        Assert.Null(ServerHostKeyEnrollment.Verify(ServerId, ServerHostKeyEnrollment.CreateSecret(), observation));

        var other = "SHA256:" + Convert.ToBase64String(SHA256.HashData(Ed25519Blob())).TrimEnd('=');
        // A conflicting operator entry must not widen enrolled trust.
        var pins = await sshd.WriteAsync("host-pins", $"127.0.0.1 {other}\n");
        var options = new SshDeploymentOptions { Port = sshd.Port, HostKeyPinsFile = pins, ConnectionTimeout = TimeSpan.FromSeconds(10) };
        var trusted = new SshDeploymentClient(options, null, new FixedPins(fingerprint));
        Assert.Equal(0, await trusted.ExecuteCommandAsync("127.0.0.1", Environment.UserName, sshd.ClientPrivateKey, "true",
            (_, _) => Task.CompletedTask, CancellationToken.None));

        await sshd.WriteAsync("host-pins", $"127.0.0.1 {fingerprint}\n");
        var mismatch = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => new SshDeploymentClient(options, null, new FixedPins(other))
            .ExecuteCommandAsync("127.0.0.1", Environment.UserName, sshd.ClientPrivateKey, "true", (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("ssh_host_key_mismatch", mismatch.FailureCode);
        Assert.Contains(sshd.Fingerprint, mismatch.Message);
    }

    internal sealed class FixedPins(string? fingerprint) : ISshHostKeyPinSource
    {
        public Task<string?> GetPinnedFingerprintAsync(SshTarget target, CancellationToken cancellationToken) => Task.FromResult(fingerprint);
    }

    internal static DirectoryInfo CreateFixtureDirectory(string prefix)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Minicloud.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found for disposable test artifacts.");
        }
        return Directory.CreateDirectory(Path.Combine(root.FullName, ".local", "artifacts", prefix + Guid.NewGuid().ToString("N")));
    }

    private static async Task<string> RunAsync(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    internal sealed class LocalSshd : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory;
        private Process? _process;

        private LocalSshd(DirectoryInfo directory, int port) { _directory = directory; Port = port; }

        public int Port { get; }
        public string HostPublicKeyPath => Path.Combine(_directory.FullName, "host_key.pub");
        public string ClientPrivateKey { get; private set; } = "";
        public string Fingerprint { get; private set; } = "";

        public static async Task<LocalSshd> StartAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var sshd = new LocalSshd(CreateFixtureDirectory("sshd-"), port);
            var dir = sshd._directory.FullName;
            await RunAsync("/usr/bin/ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(dir, "host_key"));
            await RunAsync("/usr/bin/ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(dir, "client_key"));
            File.Copy(Path.Combine(dir, "client_key.pub"), Path.Combine(dir, "authorized_keys"));
            sshd.ClientPrivateKey = await File.ReadAllTextAsync(Path.Combine(dir, "client_key"));
            sshd.Fingerprint = (await RunAsync("/usr/bin/ssh-keygen", "-l", "-E", "sha256", "-f", sshd.HostPublicKeyPath)).Split(' ')[1];
            return sshd;
        }

        public async Task<string> WriteAsync(string name, string content)
        {
            var path = Path.Combine(_directory.FullName, name);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task ListenAsync()
        {
            var dir = _directory.FullName;
            var config = await WriteAsync("sshd_config", $"""
                Port {Port}
                ListenAddress 127.0.0.1
                HostKey {dir}/host_key
                Banner {dir}/banner
                PidFile {dir}/sshd.pid
                AuthorizedKeysFile {dir}/authorized_keys
                StrictModes no
                UsePAM no
                PasswordAuthentication no
                KbdInteractiveAuthentication no
                PerSourcePenalties no

                """);
            var start = new ProcessStartInfo("/usr/sbin/sshd") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-D", "-e", "-f", config }) { start.ArgumentList.Add(argument); }
            _process = Process.Start(start)!;
            _ = _process.StandardError.ReadToEndAsync();
            _ = _process.StandardOutput.ReadToEndAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, Port);
                    return;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline && !_process.HasExited)
                {
                    await Task.Delay(100);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
            }
            _process?.Dispose();
            _directory.Delete(true);
        }
    }
}

public sealed class SshdFactAttribute : FactAttribute
{
    public SshdFactAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/sbin/sshd") || !File.Exists("/usr/bin/ssh-keygen") ||
            !File.Exists("/usr/bin/python3"))
        {
            Skip = "Requires local OpenSSH server, ssh-keygen and python3 for the host-key enrollment regression.";
        }
    }
}
