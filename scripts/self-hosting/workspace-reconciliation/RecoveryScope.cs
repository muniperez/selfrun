using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkspaceReconciliation;

public sealed record RecoveryScope(string OrganizationId, string WorkspaceId, string ProviderAccountId,
    string CredentialGeneration, string Region, string VmId, long Generation, string AllocationOperationId,
    string StopOperationId, string RecoveryOperationId, string ServerId, string VolumeId, int VolumeSizeGb,
    string? PrimaryIpId, string? FirewallId, string ProjectWitnessSshKeyId, string ProjectWitnessFingerprint,
    string AuthorizationReference)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public void Validate()
    {
        foreach (var id in new[] { OrganizationId, WorkspaceId, ProviderAccountId, CredentialGeneration, Region, VmId,
                     AllocationOperationId, StopOperationId, RecoveryOperationId })
            if (!Regex.IsMatch(id, @"\A[A-Za-z0-9](?:[A-Za-z0-9_.-]{0,61}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant))
                throw new RecoveryRefused("Scope contains an invalid Minicloud identifier.");
        foreach (var id in new[] { ServerId, VolumeId, ProjectWitnessSshKeyId, PrimaryIpId, FirewallId }.OfType<string>())
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) || numeric <= 0
                || numeric.ToString(CultureInfo.InvariantCulture) != id)
                throw new RecoveryRefused("Scope contains a noncanonical Hetzner resource ID.");
        if (Generation <= 0 || VolumeSizeGb <= 0
            || new[] { StopOperationId, AllocationOperationId, RecoveryOperationId }.Distinct().Count() != 3
            || !Regex.IsMatch(ProjectWitnessFingerprint, @"\A(?:[a-f0-9]{2}:){15}[a-f0-9]{2}\z", RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(AuthorizationReference) || AuthorizationReference.Length > 256
            || AuthorizationReference.Any(char.IsControl))
            throw new RecoveryRefused("Scope requires a generation, volume size, distinct operations, SSH witness fingerprint and authorization reference.");
    }
}

public sealed class RecoveryRefused(string message) : Exception(message);
