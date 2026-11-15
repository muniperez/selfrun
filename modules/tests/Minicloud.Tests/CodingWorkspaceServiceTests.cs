using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class CodingWorkspaceServiceTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;
    private static int _nextId;

    public CodingWorkspaceServiceTests(SqliteTestHarness harness) => _harness = harness;

    [Fact]
    public async Task CreateIsIdempotentForCompatibleConfigurationAndRejectsConflicts()
    {
        await using var db = _harness.CreateDbContext();
        var (organizationId, keyId) = await SeedAsync(db);
        var clock = TimeProvider.System;
        var service = new CodingWorkspaceService(db,
            new ProviderCatalogService(db, new IdGenerator(), clock), new IdGenerator(), clock);
        var request = new CreateCodingWorkspaceRequest(organizationId, "coding", "medium", "codex",
            "muniventures/minicloud", keyId);

        var created = await service.CreateAsync(request, CancellationToken.None);
        var resumed = await service.CreateAsync(request, CancellationToken.None);

        Assert.False(created.Resumed);
        Assert.True(resumed.Resumed);
        Assert.Equal(created.Workspace.Id, resumed.Workspace.Id);
        Assert.Equal(created.Operation.Id, resumed.Operation.Id);
        Assert.Equal("requested", created.Operation.Stage);
        Assert.Equal(CodingWorkspaceService.T3Version, created.Workspace.T3Version);
        Assert.Equal("pending", created.Workspace.AgentVersion);
        Assert.Single(db.CodingWorkspaceOperations);

        var conflict = await Assert.ThrowsAsync<CodingWorkspaceException>(() =>
            service.CreateAsync(request with { Repository = "muniventures/another-repo" }, CancellationToken.None));
        Assert.Equal("workspace_configuration_conflict", conflict.Code);
    }

    [Fact]
    public async Task CreateRequiresARegisteredKeyFromTheSelectedProviderAccount()
    {
        await using var db = _harness.CreateDbContext();
        var (organizationId, _) = await SeedAsync(db);
        var clock = TimeProvider.System;
        var service = new CodingWorkspaceService(db,
            new ProviderCatalogService(db, new IdGenerator(), clock), new IdGenerator(), clock);

        var error = await Assert.ThrowsAsync<CodingWorkspaceException>(() => service.CreateAsync(
            new CreateCodingWorkspaceRequest(organizationId, "coding", "medium", "codex",
                "muniventures/minicloud", "sshkey_other_account"), CancellationToken.None));

        Assert.Equal("ssh_key_unavailable", error.Code);
        Assert.Empty(db.CodingWorkspaces);
    }

    private static async Task<(string OrganizationId, string KeyId)> SeedAsync(Minicloud.Common.Data.MinicloudDbContext db)
    {
        var suffix = Interlocked.Increment(ref _nextId);
        var organizationId = $"org_coding_{suffix}";
        var providerId = $"provider_coding_{suffix}";
        var planId = $"plan_coding_{suffix}";
        var keyId = $"sshkey_coding_{suffix}";
        var now = DateTimeOffset.UtcNow;
        db.Organizations.Add(new Organization
        {
            Id = organizationId, Name = "Coding", Slug = $"coding-{suffix}", CreatedAt = now, UpdatedAt = now
        });
        db.ProviderAccounts.Add(new ProviderAccount
        {
            Id = providerId, OrganizationId = organizationId, Name = "Test provider",
            ProviderType = CloudProvider.Hetzner, Enabled = true, CredentialPath = "synthetic",
            CredentialGeneration = "generation-1", ValidationStatus = "valid", CreatedAt = now, UpdatedAt = now
        });
        db.ProviderPlans.Add(new ProviderPlan
        {
            Id = planId, OrganizationId = organizationId, ProviderAccountId = providerId,
            Name = "medium", NormalizedName = "MEDIUM", Region = "fsn1", Sku = "cx22",
            Image = "debian-13", BuildPlatform = "linux/amd64", Enabled = true,
            CreatedAt = now, UpdatedAt = now
        });
        db.ProviderPlanNames.Add(new ProviderPlanName
        {
            OrganizationId = organizationId, NormalizedName = "MEDIUM", PlanId = planId
        });
        db.ProviderSshKeys.Add(new ProviderSshKey
        {
            Id = keyId, OrganizationId = organizationId, ProviderAccountId = providerId,
            ProviderKeyId = "external-key", Name = "coding", PublicKey = "ssh-ed25519 synthetic",
            Fingerprint = "SHA256:synthetic", KeyType = "ssh-ed25519", RegistrationState = "ready",
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (organizationId, keyId);
    }
}
