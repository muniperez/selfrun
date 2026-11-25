namespace Selfrun.Api.Auth;

public sealed class KratosOptions
{
    public const string SectionName = "Kratos";

    public string PublicBaseUrl { get; set; } = "";
    public string AdminBaseUrl { get; set; } = "";
    public string DashboardOrigin { get; set; } = "";
    public bool GoogleEnabled { get; set; }
    public bool EmailRecoveryEnabled { get; set; }
    public bool PasskeyEnabled { get; set; }
}

public sealed class KratosUnavailableException() : Exception("The identity service is temporarily unavailable.");

/// <param name="ExpiresAt">Upstream session expiry; null when the upstream did not report one.</param>
public sealed record KratosSessionState(DateTimeOffset? ExpiresAt);

public interface IKratosSessionValidator
{
    Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken);

    /// <summary>Returns the active upstream session bound to the identity, or null when it is inactive or unbound.</summary>
    async Task<KratosSessionState?> GetActiveSessionAsync(string sessionId, string identityId, CancellationToken cancellationToken) =>
        await IsSessionActiveAsync(sessionId, identityId, cancellationToken) ? new KratosSessionState(null) : null;

    /// <summary>Extends an active upstream session by the Kratos <c>session.lifespan</c> and returns its new state.</summary>
    Task<KratosSessionState?> ExtendSessionAsync(string sessionId, string identityId, CancellationToken cancellationToken) =>
        GetActiveSessionAsync(sessionId, identityId, cancellationToken);

    Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken);
}
