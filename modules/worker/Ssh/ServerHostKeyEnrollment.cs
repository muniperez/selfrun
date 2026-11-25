using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Selfrun.Worker.Ssh;

/// <summary>
/// First-boot SSH host-key enrollment for worker-provisioned servers. The worker supplies a one-time
/// secret through the server's provider-private cloud-init. After the image generates its permanent
/// Ed25519 host key, cloud-init publishes <c>HMAC-SHA256(secret, protocol \n serverId \n key)</c> in
/// an SSH pre-authentication banner. The worker trusts the key only after that proof verifies.
/// </summary>
public static class ServerHostKeyEnrollment
{
    // Distinct from the seed wizard's "selfrun-seed-host-v1" label.
    public const string Protocol = "selfrun-server-host-v1";
    public const string HostKeyAlgorithm = "ssh-ed25519";
    public const int SecretLength = 32;
    internal const string ScriptPath = "/run/selfrun/host-enrollment.py";

    public static byte[] CreateSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static bool IsValidServerId(string serverId) => Regex.IsMatch(serverId, "\\A[0-9a-f]{32}\\z");

    public static string Fingerprint(byte[] hostKeyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKeyBlob)).TrimEnd('=');

    internal static byte[] Message(string serverId, byte[] hostKeyBlob) =>
        Encoding.ASCII.GetBytes($"{Protocol}\n{serverId}\n{HostKeyAlgorithm} {Convert.ToBase64String(hostKeyBlob)}");

    /// <summary>
    /// Returns the verified <c>SHA256:</c> fingerprint, or <c>null</c> when the observed Ed25519 key is not
    /// bound to <paramref name="serverId"/> by exactly one valid proof in the banner.
    /// </summary>
    public static string? Verify(string serverId, byte[] secret, SshHostKeyObservation? observation)
    {
        if (observation is null || secret.Length != SecretLength || !IsValidServerId(serverId) ||
            observation.Algorithm != HostKeyAlgorithm || !IsEd25519Blob(observation.KeyBlob) ||
            string.IsNullOrEmpty(observation.Banner))
        {
            return null;
        }

        var prefix = Protocol + " ";
        var proofs = observation.Banner.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (proofs.Length != 1 || proofs[0].Length != prefix.Length + 64)
        {
            return null;
        }

        Span<byte> actual = stackalloc byte[32];
        if (Convert.FromHexString(proofs[0].AsSpan(prefix.Length), actual, out _, out var written) !=
            System.Buffers.OperationStatus.Done || written != 32)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(secret, Message(serverId, observation.KeyBlob));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? Fingerprint(observation.KeyBlob) : null;
    }

    // SSH wire format: string "ssh-ed25519" followed by a 32-byte public key string.
    private static bool IsEd25519Blob(byte[] blob)
    {
        var name = Encoding.ASCII.GetBytes(HostKeyAlgorithm);
        if (blob.Length != 4 + name.Length + 4 + 32) { return false; }
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob) == name.Length &&
            blob.AsSpan(4, name.Length).SequenceEqual(name) &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(4 + name.Length)) == 32;
    }

    /// <summary>
    /// Python first-boot script. It runs from cloud-init <c>runcmd</c>, after the stock SSH module has
    /// generated the host keys, and changes nothing but an added sshd Banner drop-in.
    /// </summary>
    public static string CloudInitScript(string serverId, byte[] secret)
    {
        if (!IsValidServerId(serverId) || secret.Length != SecretLength)
        {
            throw new ArgumentException("Invalid SSH host enrollment material.");
        }

        return $$"""
#!/usr/bin/python3
import hashlib, hmac, pathlib, subprocess

PROTOCOL = "{{Protocol}}"
SERVER_ID = "{{serverId}}"
SECRET = bytes.fromhex("{{Convert.ToHexString(secret).ToLowerInvariant()}}")

def proof(public_key_line):
    key_type, blob, *_ = public_key_line.split()
    if key_type != "{{HostKeyAlgorithm}}":
        raise RuntimeError("Expected the cloud-init Ed25519 host public key.")
    message = f"{PROTOCOL}\n{SERVER_ID}\n{key_type} {blob}".encode("ascii")
    return hmac.new(SECRET, message, hashlib.sha256).hexdigest()

def effective_banner():
    settings = subprocess.check_output(["/usr/sbin/sshd", "-T"], text=True)
    return next(line.split(maxsplit=1)[1] for line in settings.splitlines() if line.startswith("banner "))

def main():
    value = proof(pathlib.Path("/etc/ssh/ssh_host_ed25519_key.pub").read_text())
    banner = pathlib.Path("/etc/ssh/selfrun-host-enrollment")
    config = pathlib.Path("/etc/ssh/sshd_config.d/01-selfrun-host-enrollment.conf")
    pathlib.Path("/run/sshd").mkdir(mode=0o755, exist_ok=True)
    if effective_banner() != "none":
        raise RuntimeError("Existing SSH banner retained; host enrollment blocked.")
    with banner.open("x") as stream:
        stream.write(f"{PROTOCOL} {value}\n")
    with config.open("x") as stream:
        stream.write(f"Banner {banner}\n")
    try:
        if effective_banner() != str(banner):
            raise RuntimeError("SSH configuration did not activate host enrollment.")
        subprocess.run(["systemctl", "reload-or-restart", "ssh.service"], check=True)
    except (RuntimeError, subprocess.SubprocessError, OSError):
        config.unlink()
        raise

if __name__ == "__main__":
    main()

""";
    }
}

/// <summary>
/// An Ed25519 host key whose possession the server proved during SSH key exchange,
/// plus the pre-authentication banner received over that session.
/// </summary>
public sealed record SshHostKeyObservation(string Algorithm, byte[] KeyBlob, string? Banner);

public interface IServerHostKeyProbe
{
    /// <summary>
    /// Connects without offering any credential and returns the observed host key and banner,
    /// or <c>null</c> when the server is not reachable or offers no Ed25519 host key.
    /// </summary>
    Task<SshHostKeyObservation?> ObserveAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class SshNetServerHostKeyProbe : IServerHostKeyProbe
{
    public async Task<SshHostKeyObservation?> ObserveAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // "none" is the only method: SSH.NET sends it first and then stops because nothing else is offered.
        var info = new ConnectionInfo(host, port, "selfrun", new NoneAuthenticationMethod("selfrun")) { Timeout = timeout };
        foreach (var name in info.HostKeyAlgorithms.Keys.Where(name => name != ServerHostKeyEnrollment.HostKeyAlgorithm).ToArray())
        {
            info.HostKeyAlgorithms.Remove(name);
        }

        var banner = new StringBuilder();
        info.AuthenticationBanner += (_, e) => { lock (banner) { banner.Append(e.BannerMessage); } };
        string? algorithm = null;
        byte[]? key = null;
        using var client = new SshClient(info);
        // SSH.NET raises this only after the server's exchange-hash signature verifies for the key.
        client.HostKeyReceived += (_, e) =>
        {
            algorithm = e.HostKeyName;
            key = e.HostKey;
            e.CanTrust = e.HostKeyName == ServerHostKeyEnrollment.HostKeyAlgorithm && e.Certificate is null;
        };
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (SshAuthenticationException)
        {
            // Expected: no credential was offered.
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException)
        {
            return null;
        }
        finally
        {
            try { client.Disconnect(); } catch (Exception) { /* best effort */ }
        }

        if (algorithm != ServerHostKeyEnrollment.HostKeyAlgorithm || key is null) { return null; }
        lock (banner) { return new SshHostKeyObservation(algorithm, key, banner.ToString()); }
    }
}
