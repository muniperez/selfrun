using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minicloud.Worker.DevelopmentWorkspaces;

internal sealed record WorkspaceBootstrapSecret(string HostPrivateKey, string HostPublicKey, string HostFingerprint, string EnrollmentToken);

internal static class WorkspaceBootstrap
{
    public static WorkspaceBootstrapSecret Generate()
    {
        using var rsa = RSA.Create(3072);
        var parameters = rsa.ExportParameters(false);
        using var encoded = new MemoryStream();
        WriteString(encoded, Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteInteger(encoded, parameters.Exponent!);
        WriteInteger(encoded, parameters.Modulus!);
        var blob = encoded.ToArray();
        return new(rsa.ExportRSAPrivateKeyPem(), "ssh-rsa " + Convert.ToBase64String(blob),
            "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }

    public static string CloudConfig(WorkspaceBootstrapSecret secret, string authorizedKey)
    {
        if (authorizedKey.Length > 8192 || authorizedKey.Contains('\n') || authorizedKey.Contains('\r') ||
            !(authorizedKey.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) || authorizedKey.StartsWith("ssh-rsa ", StringComparison.Ordinal)))
            throw new InvalidOperationException("An explicit management SSH public key is required.");
        return "#cloud-config\n" + JsonSerializer.Serialize(new
        {
            disable_root = false, ssh_pwauth = false, ssh_deletekeys = true,
            ssh_genkeytypes = Array.Empty<string>(),
            ssh_keys = new { rsa_private = secret.HostPrivateKey, rsa_public = secret.HostPublicKey },
            users = new[] { new { name = "root", lock_passwd = true, ssh_authorized_keys = new[] { authorizedKey } } },
            write_files = new[]
            {
                new
                {
                    path = "/etc/ssh/sshd_config.d/90-minicloud-workspace.conf", owner = "root:root", permissions = "0600",
                    content = "PasswordAuthentication no\nKbdInteractiveAuthentication no\nPermitRootLogin prohibit-password\nAllowUsers root\nAllowAgentForwarding no\nX11Forwarding no\nGatewayPorts no\nPermitOpen 127.0.0.1:*\n"
                }
            },
            runcmd = new[] { new[] { "systemctl", "restart", "ssh" } }
        });
    }

    private static void WriteInteger(Stream stream, byte[] value)
    {
        if ((value[0] & 128) != 0) value = [0, .. value];
        WriteString(stream, value);
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, checked((uint)value.Length));
        stream.Write(size); stream.Write(value);
    }

    public static string Generation(long generation) => generation.ToString(CultureInfo.InvariantCulture);
}
