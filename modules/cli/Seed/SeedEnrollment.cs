// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Minicloud.Cli.Commands;

namespace Minicloud.Cli.Seed;

internal static class SeedEnrollment
{
    private const string Protocol = "minicloud-seed-host-v1";

    internal static byte[] CreateSecret(string path)
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        SeedFiles.WriteNew(pending, secret);
        SeedJournal.PublishDurably(pending, path, overwrite: false);
        return secret;
    }

    internal static byte[] ReadSecret(string path)
    {
        SeedGuard.Require(File.Exists(path), "Original SSH enrollment secret is missing. Preserve this target and reconcile; no authenticated SSH was attempted.");
        SeedFiles.Check(path);
        SeedGuard.Require(new FileInfo(path).Length == 32, "Invalid SSH enrollment secret. Restore the original protected state.");
        return File.ReadAllBytes(path);
    }

    internal static string UserData(string seedId, byte[] secret)
    {
        SeedGuard.Require(Regex.IsMatch(seedId, "\\A[0-9a-f]{32}\\z") && secret.Length == 32, "Invalid SSH enrollment material.");
        // A cloud-init user script runs after the stock SSH module generates the host keys.
        // Do not change cloud-init SSH settings or replace any provider key/configuration.
        return $$"""
#!/usr/bin/python3
import hashlib, hmac, pathlib, subprocess

def effective_banner():
    settings = subprocess.check_output(["/usr/sbin/sshd", "-T"], text=True)
    return next(line.split(maxsplit=1)[1] for line in settings.splitlines() if line.startswith("banner "))

key_type, blob, *_ = pathlib.Path("/etc/ssh/ssh_host_ed25519_key.pub").read_text().split()
if key_type != "ssh-ed25519":
    raise RuntimeError("Expected the cloud-init Ed25519 host public key.")
protocol = "{{Protocol}}"
message = f"{protocol}\n{{seedId}}\n{key_type} {blob}".encode("ascii")
proof = hmac.new(bytes.fromhex("{{Convert.ToHexString(secret)}}"), message, hashlib.sha256).hexdigest()

banner = pathlib.Path("/etc/ssh/minicloud-seed-enrollment")
config = pathlib.Path("/etc/ssh/sshd_config.d/01-minicloud-seed-enrollment.conf")
pathlib.Path("/run/sshd").mkdir(mode=0o755, exist_ok=True)
if effective_banner() != "none":
    raise RuntimeError("Existing SSH banner retained; seed enrollment blocked.")
with banner.open("x") as stream:
    stream.write(f"{protocol} {proof}\n")
with config.open("x") as stream:
    stream.write(f"Banner {banner}\n")
try:
    if effective_banner() != str(banner):
        raise RuntimeError("SSH configuration did not activate seed enrollment.")
    subprocess.run(["systemctl", "reload-or-restart", "ssh.service"], check=True)
except (RuntimeError, subprocess.SubprocessError, OSError):
    config.unlink()
    raise

""" + "\n";
    }

    internal static async Task<string?> VerifyAsync(string seedId, string address, byte[] secret, string home, CancellationToken ct)
    {
        SeedGuard.Require(IPAddress.TryParse(address, out var ipv4) && ipv4.AddressFamily == AddressFamily.InterNetwork
            && ipv4.ToString() == address, "The original server does not have a canonical IPv4 address.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var scratch = Path.Combine(home, ".seed-proof-" + Guid.NewGuid().ToString("N"));
        SeedFiles.Directory(scratch);
        try
        {
            while (true)
            {
                var fingerprint = await ProbeOnceAsync(seedId, address, secret, scratch, deadline.Token);
                if (fingerprint is not null) return fingerprint;
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    internal static async Task<string?> ProbeOnceAsync(string seedId, string address, byte[] secret, string scratch, CancellationToken ct)
    {
        var scan = await SeedProcess.ProbeAsync("ssh-keyscan", ["-T", "5", "-t", "ed25519", address], ct);
        var candidates = scan.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).Distinct().ToArray();
        if (scan.ExitCode != 0 || candidates.Length != 1) return null;
        var candidate = candidates[0].Trim();
        string fingerprint;
        try { fingerprint = SeedHost.Fingerprint(candidate, address); }
        catch (CliCommandException) { return null; } // An untrusted scan may be malformed or not ready.

        var blob = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
        var key = "ssh-ed25519 " + Convert.ToBase64String(Convert.FromBase64String(blob));
        var mac = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes($"{Protocol}\n{seedId}\n{key}"));
        var knownHosts = Path.Combine(scratch, "known_hosts");
        SeedFiles.WriteNew(knownHosts, Encoding.ASCII.GetBytes($"{address} {key}\n"));
        try
        {
            // ssh verifies possession of the exact candidate before userauth. No credential is offered.
            var probe = await SeedProcess.ProbeAsync("ssh", SeedHost.EnrollmentProbeOptions(scratch, address), ct);
            var prefix = Protocol + " ";
            var proofs = probe.Error.Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (probe.ExitCode != 255 || proofs.Length != 1 || proofs[0].Length != prefix.Length + 64) return null;
            Span<byte> actual = stackalloc byte[32];
            return Convert.FromHexString(proofs[0].AsSpan(prefix.Length), actual, out _, out _) == System.Buffers.OperationStatus.Done
                && CryptographicOperations.FixedTimeEquals(mac, actual) ? fingerprint : null;
        }
        finally { File.Delete(knownHosts); }
    }
}
