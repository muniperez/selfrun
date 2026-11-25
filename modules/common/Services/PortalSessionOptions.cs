using Microsoft.Extensions.Options;

namespace Selfrun.Common.Services;

/// <summary>
/// Operator policy for dashboard (portal) browser sessions. Durations are whole minutes so the same values can be
/// passed to the installer, the API, and the Kratos session lifespan without format conversion.
/// </summary>
public sealed class PortalSessionOptions
{
    public const string SectionName = "PortalSessions";
    public const int MinimumMinutes = 1;
    public const int MaximumIdleMinutes = 30 * 24 * 60;
    public const int MaximumLifetimeMinutes = 90 * 24 * 60;

    /// <summary>Whether users may opt in to "Keep me signed in" remembered sessions.</summary>
    public bool KeepSignedInEnabled { get; set; } = true;
    public int StandardIdleMinutes { get; set; } = 120;
    public int StandardMaxMinutes { get; set; } = 12 * 60;
    public int RememberedIdleMinutes { get; set; } = 7 * 24 * 60;
    public int RememberedMaxMinutes { get; set; } = 30 * 24 * 60;

    public TimeSpan IdleTimeout(bool remembered) =>
        TimeSpan.FromMinutes(remembered ? RememberedIdleMinutes : StandardIdleMinutes);

    public TimeSpan MaxLifetime(bool remembered) =>
        TimeSpan.FromMinutes(remembered ? RememberedMaxMinutes : StandardMaxMinutes);

    public IEnumerable<string> Validate()
    {
        foreach (var (name, idle, max) in new[]
                 {
                     ("Standard", StandardIdleMinutes, StandardMaxMinutes),
                     ("Remembered", RememberedIdleMinutes, RememberedMaxMinutes)
                 })
        {
            if (idle is < MinimumMinutes or > MaximumIdleMinutes)
                yield return $"{SectionName}:{name}IdleMinutes must be between {MinimumMinutes} and {MaximumIdleMinutes}.";
            if (max < idle || max > MaximumLifetimeMinutes)
                yield return $"{SectionName}:{name}MaxMinutes must be at least the idle timeout and at most {MaximumLifetimeMinutes}.";
        }
        if (RememberedIdleMinutes < StandardIdleMinutes || RememberedMaxMinutes < StandardMaxMinutes)
            yield return $"{SectionName} remembered durations must not be shorter than standard durations.";
    }
}

public sealed class PortalSessionOptionsValidator : IValidateOptions<PortalSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, PortalSessionOptions options)
    {
        var failures = options.Validate().ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
