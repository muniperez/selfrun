using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.CodingWorkspaces;

namespace Minicloud.Worker.Tests;

public sealed class CodingWorkspaceOperationProcessorTests(WorkerSqliteTestHarness harness)
    : IClassFixture<WorkerSqliteTestHarness>
{
    [Fact]
    public void ServerReconciliationRequiresEveryWorkspaceOwnershipLabel()
    {
        var workspace = new CodingWorkspace
        {
            Id = "workspace_synthetic",
            OrganizationId = "org_synthetic"
        };
        var labels = new Dictionary<string, string>
        {
            ["workspace"] = workspace.Id,
            ["organization"] = workspace.OrganizationId,
            ["managed-by"] = "minicloud"
        };
        var server = new ProviderServerInfo(
            "server_synthetic", "minicloud-coding-workspace_synthetic", "192.0.2.1", "running", Labels: labels);

        Assert.True(CodingWorkspaceOperationProcessor.MatchesWorkspace(server, workspace));
        Assert.False(CodingWorkspaceOperationProcessor.MatchesWorkspace(server with { Labels = null }, workspace));
        Assert.False(CodingWorkspaceOperationProcessor.MatchesWorkspace(server with
        {
            Labels = new Dictionary<string, string> { ["workspace"] = workspace.Id }
        }, workspace));
        Assert.False(CodingWorkspaceOperationProcessor.MatchesWorkspace(server with
        {
            Labels = new Dictionary<string, string>(labels) { ["organization"] = "org_other" }
        }, workspace));
    }

    [Fact]
    public async Task ExpiredOperationIsPersistedWhenNoQueuedOperationExists()
    {
        await using var db = harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var organizationId = "org_workspace_recovery";
        var providerId = "provider_workspace_recovery";
        var planId = "plan_workspace_recovery";
        var keyId = "sshkey_workspace_recovery";
        db.Organizations.Add(new Organization
        {
            Id = organizationId, Name = "Recovery", Slug = "workspace-recovery",
            CreatedAt = now, UpdatedAt = now
        });
        db.ProviderAccounts.Add(new ProviderAccount
        {
            Id = providerId, OrganizationId = organizationId, Name = "Test",
            ProviderType = CloudProvider.Hetzner, Enabled = true,
            CredentialPath = "synthetic", CredentialGeneration = "generation-1",
            ValidationStatus = "valid", CreatedAt = now, UpdatedAt = now
        });
        db.ProviderPlans.Add(new ProviderPlan
        {
            Id = planId, OrganizationId = organizationId, ProviderAccountId = providerId,
            Name = "medium", NormalizedName = "MEDIUM", Region = "fsn1", Sku = "cx22",
            Image = "ubuntu-24.04", BuildPlatform = "linux/amd64", Enabled = true,
            CreatedAt = now, UpdatedAt = now
        });
        db.ProviderPlanNames.Add(new ProviderPlanName
        {
            OrganizationId = organizationId, NormalizedName = "MEDIUM", PlanId = planId
        });
        db.ProviderSshKeys.Add(new ProviderSshKey
        {
            Id = keyId, OrganizationId = organizationId, ProviderAccountId = providerId,
            ProviderKeyId = "provider-key", Name = "coding", PublicKey = "ssh-ed25519 synthetic",
            Fingerprint = "SHA256:synthetic", KeyType = "ssh-ed25519", RegistrationState = "ready",
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var clock = TimeProvider.System;
        var workspaces = new CodingWorkspaceService(
            db, new ProviderCatalogService(db, new IdGenerator(), clock), new IdGenerator(), clock);
        var created = await workspaces.CreateAsync(new CreateCodingWorkspaceRequest(
            organizationId, "coding", "medium", "codex", "owner/repo", keyId), CancellationToken.None);
        created.Workspace.Stage = "awaiting-auth";
        created.Operation.Status = "completed";
        created.Operation.CompletedAt = now;
        await db.SaveChangesAsync();

        var authOperation = await workspaces.StartAuthenticationAsync(
            organizationId, created.Workspace.Id, "codex", "actor", CancellationToken.None);
        authOperation.LeaseExpiresAt = now.AddMinutes(-1);
        await db.SaveChangesAsync();

        var clients = new ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!);
        var processor = new CodingWorkspaceOperationProcessor(db, clients, clock);
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));

        db.ChangeTracker.Clear();
        var recovered = await db.CodingWorkspaceOperations.FindAsync(authOperation.Id);
        var workspace = await db.CodingWorkspaces.FindAsync(created.Workspace.Id);
        Assert.Equal("failed", recovered!.Status);
        Assert.Equal("authorization_interrupted", recovered.ErrorCode);
        Assert.Equal("failed", workspace!.CodexAuthentication);
        Assert.NotNull(workspace.CodexAuthenticationObservedAt);
    }
}
