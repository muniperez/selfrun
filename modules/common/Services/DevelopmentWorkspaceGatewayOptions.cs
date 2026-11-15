using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Globalization;

namespace Minicloud.Common.Services;

public sealed class DevelopmentWorkspaceGatewayOptions
{
    public const string SectionName = "DevelopmentWorkspaceGateway";
    public bool Enabled { get; set; }
    public string GatewayDomain { get; set; } = "";
    public string ServiceCredential { get; set; } = "";
    public string ServiceCredentialFile { get; set; } = "";
    public string ControlPlaneUrl { get; set; } = "";
    public string RelayHost { get; set; } = "127.0.0.1";
    public string TrustedProxyAddress { get; set; } = "";
    public int MinimumRelayPort { get; set; } = 42000;
    public int MaximumRelayPort { get; set; } = 42999;
    public int LaunchLifetimeSeconds { get; set; } = 60;
    public int SessionLifetimeSeconds { get; set; } = 1800;
    public int MaxConnections { get; set; } = 128;
    public int MaxConnectionsPerSession { get; set; } = 16;
    public int MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxWebSocketMessageBytes { get; set; } = 1024 * 1024;
    public int ConnectionLifetimeSeconds { get; set; } = 1800;
    public int HttpTimeoutSeconds { get; set; } = 60;
    public int ReauthorizationSeconds { get; set; } = 5;

    public bool IsConfigured => Enabled && ServiceCredential.Length is >= 32 and <= 512 &&
        !ServiceCredential.Any(char.IsWhiteSpace) && !ServiceCredential.Any(char.IsControl) &&
        Regex.IsMatch(GatewayDomain, @"^(?=.{1,190}$)(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant) &&
        IsRelayHost(RelayHost) && MinimumRelayPort >= 1024 && MaximumRelayPort <= 65535 && MinimumRelayPort <= MaximumRelayPort;

    public static bool IsRelayHost(string host) =>
        IPAddress.TryParse(host, out _) ? IsPrivateAddress(host) : Uri.CheckHostName(host) == UriHostNameType.Dns;

    public void LoadServiceCredential()
    {
        if (!Enabled || string.IsNullOrEmpty(ServiceCredentialFile)) return;
        if (!Path.IsPathRooted(ServiceCredentialFile))
            throw new InvalidOperationException("Gateway credential file must be an absolute protected path.");
        using var stream = File.OpenRead(ServiceCredentialFile);
        if (stream.Length > 1024)
            throw new InvalidOperationException("Gateway credential file exceeds its limit.");
        using var reader = new StreamReader(stream);
        var buffer = new char[1025];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 1024) throw new InvalidOperationException("Gateway credential file exceeds its limit.");
        ServiceCredential = new string(buffer, 0, count).TrimEnd('\r', '\n');
        if (ServiceCredential.Length is < 32 or > 512 || ServiceCredential.Any(char.IsWhiteSpace) ||
            ServiceCredential.Any(char.IsControl))
            throw new InvalidOperationException("Gateway credential file does not contain a valid credential.");
    }

    public bool TryParseHost(string host, out long generation, out string service)
    {
        generation = 0;
        service = "t3";
        if (!AcceptHost(host)) return false;
        var label = host[..^(GatewayDomain.Length + 1)];
        var match = Regex.Match(label, @"^vm-[a-f0-9]{32}-g([1-9][0-9]*)(?:-s-([a-z][a-z0-9-]{0,15}))?$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out generation))
            return false;
        if (match.Groups[2].Success) service = match.Groups[2].Value;
        return true;
    }

    public static bool IsPrivateAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : (bytes[0] & 0xfe) == 0xfc;
    }

    public string? Host(string vmId, long generation, string service)
    {
        if (!IsConfigured || generation < 1 ||
            !Regex.IsMatch(vmId, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(service, "^[a-z][a-z0-9-]{0,15}$", RegexOptions.CultureInvariant))
            return null;
        var vmLabel = Digest(vmId)[..32].ToLowerInvariant();
        var label = $"vm-{vmLabel}-g{generation}" + (service == "t3" ? "" : $"-s-{service}");
        return label.Length <= 63 ? $"{label}.{GatewayDomain}" : null;
    }

    public bool AcceptHost(string host) => IsConfigured && host.Length <= 254 &&
        host.EndsWith("." + GatewayDomain, StringComparison.Ordinal) &&
        Regex.IsMatch(host[..^(GatewayDomain.Length + 1)], @"^vm-[a-z0-9][a-z0-9-]*-g[1-9][0-9]*(?:-s-[a-z][a-z0-9-]{0,15})?$", RegexOptions.CultureInvariant) &&
        host[..^(GatewayDomain.Length + 1)].Length <= 63;

    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

public sealed record DevelopmentWorkspaceGatewayLaunchRequest(long Generation, string Service = "t3");
public sealed record DevelopmentWorkspaceGatewayLaunchResponse(string Url, DateTimeOffset ExpiresAt);
public sealed record DevelopmentWorkspaceGatewayReceiptRequest(string Host, string Receipt);
public sealed record DevelopmentWorkspaceGatewayRedemption(string Session, DateTimeOffset ExpiresAt);
public sealed record DevelopmentWorkspaceGatewayTarget(int LoopbackPort, string? Bearer);
