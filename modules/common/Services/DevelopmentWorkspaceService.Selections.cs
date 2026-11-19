using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    public async Task<WorkspaceSelections> GetSelectionsAsync(
        string organizationId, string? workspaceId, CancellationToken ct)
    {
        ValidateText(organizationId, 128);
        DevelopmentWorkspace? workspace = null;
        if (workspaceId is not null)
        {
            ValidateText(workspaceId, 128);
            workspace = await db.DevelopmentWorkspaces.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == workspaceId, ct)
                ?? throw Invalid("workspace_not_found", "Workspace was not found.");
        }

        try { EnsureEnabled(); }
        catch (DevelopmentWorkspaceException error) when (error.Code is "workspaces_disabled" or "workspace_policy_invalid")
        {
            return new(false, error.Message, []);
        }

        // Project only public catalog metadata; discovery never resolves provider credentials.
        var plans = await db.ProviderPlans.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ProviderAccount.OrganizationId == organizationId)
            .Select(x => new SelectionPlan(x.Id, x.Name, x.OrganizationId, x.ProviderAccountId,
                x.Region, x.Sku, x.BuildPlatform, x.Enabled, x.ProviderAccount.Name,
                x.ProviderAccount.OrganizationId, x.ProviderAccount.Enabled, x.ProviderAccount.ProviderType))
            .ToListAsync(ct);
        var selections = new List<WorkspaceSelectionOption>();
        foreach (var (imageId, image) in Policy.Images.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!ValidText(imageId, 128) || !EligibleImage(image, forNewWorkspace: workspace is null)) continue;
            foreach (var (profileId, profile) in Policy.Profiles.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!ValidText(profileId, 32)) continue;
                foreach (var plan in plans.OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    if (!ValidText(plan.Id, 128) || !EligibleProfile(profile, plan.Id)
                        || !EligiblePlan(organizationId, image, profile, plan)
                        || workspace is not null && !MatchesPlacement(workspace, plan.ProviderAccountId, plan.Region))
                        continue;
                    selections.Add(new(imageId, string.IsNullOrWhiteSpace(image.DisplayName) ? imageId : image.DisplayName.Trim(),
                        profileId, plan.Id, plan.Name, plan.ProviderAccountName, plan.Region, plan.Sku,
                        profile.CpuCount, profile.MemoryGiB, profile.ComputeHourlyRate,
                        workspace is null && profile.DefaultPlanIdsForNewWorkspaces.Contains(plan.Id, StringComparer.Ordinal),
                        ImageAgents(image)));
                }
            }
        }
        return new(true, null, selections, Policy.Currency, Policy.VolumeMonthlyRatePerGb);
    }

    private bool EligibleImage(DevelopmentWorkspaceImage image, bool forNewWorkspace) =>
        image.Enabled && image.RuntimeQualified && image.QualifiedAt is not null && image.QualifiedAt <= Now
        && Sha(image.ManifestSha256, 64) && !string.IsNullOrWhiteSpace(image.ProviderImageId)
        && image.Architecture == "linux/amd64" && (!forNewWorkspace || image.T3ConnectForNewWorkspaces);

    private static DevelopmentWorkspaceAgentResponse[]? ImageAgents(DevelopmentWorkspaceImage? image)
    {
        var agents = (image?.Agents ?? []).Where(x => ValidText(x.Id, 64) && ValidText(x.Name, 128) && ValidText(x.Version, 128))
            .Select(x => new DevelopmentWorkspaceAgentResponse(x.Id.Trim(), x.Name.Trim(), x.Version.Trim())).ToArray();
        return agents.Length == 0 ? null : agents;
    }

    private static bool EligibleProfile(DevelopmentWorkspaceProfile profile, string planId) =>
        profile.MemoryGiB is 8 or 16 && profile.CpuCount > 0 && profile.ComputeHourlyRate > 0
        && profile.AllowedPlanIds.Contains(planId, StringComparer.Ordinal);

    private static bool EligiblePlan(string organizationId, DevelopmentWorkspaceImage image,
        DevelopmentWorkspaceProfile profile, SelectionPlan plan) =>
        plan.OrganizationId == organizationId && plan.Enabled && plan.AccountEnabled
        && plan.ProviderType is CloudProvider.Hetzner or CloudProvider.DigitalOcean or CloudProvider.Vultr && plan.AccountOrganizationId == organizationId
        && plan.BuildPlatform == "linux/amd64" && image.ProviderAccountId == plan.ProviderAccountId
        && image.AllowedRegions.Contains(plan.Region, StringComparer.Ordinal)
        && profile.AllowedSkus.Contains(plan.Sku, StringComparer.Ordinal)
        && profile.AllowedRegions.Contains(plan.Region, StringComparer.Ordinal);

    private static bool MatchesPlacement(DevelopmentWorkspace workspace, string providerAccountId, string region) =>
        workspace.ProviderAccountId == providerAccountId && workspace.Region == region;

    private sealed record SelectionPlan(
        string Id, string Name, string OrganizationId, string ProviderAccountId, string Region, string Sku,
        string BuildPlatform, bool Enabled, string ProviderAccountName, string AccountOrganizationId,
        bool AccountEnabled, CloudProvider ProviderType);
}
