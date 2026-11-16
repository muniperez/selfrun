using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DevelopmentWorkspaceServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestClock clock = new();
    private readonly DevelopmentWorkspaceOptions policy = new()
    {
        Enabled = true, MaxActiveWorkspaces = 3, MaxActiveVms = 5, MaxWorkspacesPerOrganization = 5,
        MaxVmsPerWorkspace = 3, MaxVmsPerOrganization = 4,
        MaxEstimatedMonthlySpend = 10000, MaxEstimatedMonthlySpendPerOrganization = 5000,
        VolumeMonthlyRatePerGb = .1m, PricingUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        Repositories = [new() { OrganizationId = "org", Repository = "https://git.example.test/team/repo.git",
            AllowedBaseRefs = ["refs/heads/main"], AllowPush = true }],
        Profiles = new()
        {
            ["standard"] = new() { CpuCount = 4, MemoryGiB = 8, ComputeHourlyRate = .1m,
                AllowedPlanIds = ["plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["cx-test"] },
            ["heavy"] = new() { CpuCount = 8, MemoryGiB = 16, ComputeHourlyRate = .2m,
                AllowedPlanIds = ["heavy-plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["cx-heavy"] }
        },
        Images = new()
        {
            ["qualified"] = new() { Enabled = true, RuntimeQualified = true, ProviderAccountId = "account",
                AllowedRegions = ["nbg1"], ProviderImageId = "image-123", ManifestSha256 = new('a', 64),
                QualifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }
        }
    };
    private MinicloudDbContext db = default!;
    private DevelopmentWorkspaceService Service(MinicloudDbContext? context = null) =>
        new(context ?? db, new IdGenerator(), clock, Options.Create(policy));

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = Context();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.AddRange(new Organization { Id = "org", Name = "Org", Slug = "org" },
            new Organization { Id = "other", Name = "Other", Slug = "other" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Test",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic", CredentialGeneration = "test" });
        db.ProviderPlans.AddRange(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
                Name = "Standard", NormalizedName = "standard", Region = "nbg1", Sku = "cx-test" },
            new ProviderPlan { Id = "heavy-plan", OrganizationId = "org", ProviderAccountId = "account",
                Name = "Heavy", NormalizedName = "heavy", Region = "nbg1", Sku = "cx-heavy" });
        await db.SaveChangesAsync();
    }

    private MinicloudDbContext Context() => new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
    private static CreateDevelopmentWorkspaceRequest Create(string name = "Test", string key = "create") =>
        new("org", name, "qualified", "standard", "plan", 20, key);
    private static LaunchEphemeralDevelopmentWorkspaceVmRequest Launch(string key = "launch") =>
        new("qualified", "standard", "plan", "https://git.example.test/team/repo.git", "refs/heads/main",
            new string('b', 40), true, true, key);
    private async Task<DevelopmentWorkspace> Workspace() => await Service().CreateAsync(Create(), "actor", default);
    private static DevelopmentWorkspaceVm Primary(DevelopmentWorkspace workspace) => workspace.Vms.Single(x => x.Kind == "primary");

    [Fact]
    public async Task DiscoveryReturnsStableCompatibleSubmissionIdsAndLabelsWithoutTrackingOrMutating()
    {
        db.ChangeTracker.Clear();
        var choices = await Service().GetSelectionsAsync("org", null, default);
        Assert.True(choices.Available);
        Assert.Null(choices.UnavailableReason);
        Assert.Equal(2, choices.Options.Count);
        var standard = Assert.Single(choices.Options, x => x.Profile == "standard");
        Assert.Equal(new WorkspaceSelectionOption("qualified", "qualified", "standard", "plan",
            "Standard", "Test", "nbg1", "cx-test"), standard);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await db.DevelopmentWorkspaces.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
        Assert.All(await db.ProviderPlans.ToListAsync(), x => Assert.Null(x.ProvisioningLockedAt));

        policy.Images["qualified"].DisplayName = "  Qualified development image  ";
        var renamed = await Service().GetSelectionsAsync("org", null, default);
        Assert.All(renamed.Options, x => Assert.Equal("Qualified development image", x.ImageName));
        Assert.Equal(choices.Options.Select(x => (x.ImageId, x.Profile, x.PlanId)),
            renamed.Options.Select(x => (x.ImageId, x.Profile, x.PlanId)));
        policy.Images["qualified"].DisplayName = " ";
        Assert.All((await Service().GetSelectionsAsync("org", null, default)).Options,
            x => Assert.Equal("qualified", x.ImageName));
        foreach (var choice in choices.Options)
        {
            var workspace = await Service().CreateAsync(Create(choice.Profile, choice.Profile) with
                { ImageId = choice.ImageId, Profile = choice.Profile, PlanId = choice.PlanId }, "actor", default);
            Assert.Equal(choice.PlanId, Primary(workspace).PlanId);
        }
    }

    [Fact]
    public async Task DiscoveryDistinguishesEmptyDisabledAndInvalidConfiguration()
    {
        var foreign = await Service().GetSelectionsAsync("other", null, default);
        Assert.True(foreign.Available);
        Assert.Null(foreign.UnavailableReason);
        Assert.Empty(foreign.Options);
        policy.Enabled = false;
        var disabled = await Service().GetSelectionsAsync("org", null, default);
        Assert.False(disabled.Available);
        Assert.Contains("disabled", disabled.UnavailableReason);
        Assert.Empty(disabled.Options);
        policy.Enabled = true;
        policy.PricingUpdatedAt = clock.GetUtcNow().AddDays(-100);
        var invalid = await Service().GetSelectionsAsync("org", null, default);
        Assert.False(invalid.Available);
        Assert.Contains("pricing", invalid.UnavailableReason);
        Assert.Empty(invalid.Options);
        Assert.Empty(await db.DevelopmentWorkspaces.ToListAsync());
    }

    [Fact]
    public async Task ExistingWorkspaceDiscoveryAndWritesRespectFixedAccountRegionAndOrganization()
    {
        var workspace = await Workspace();
        db.ProviderAccounts.AddRange(
            new ProviderAccount { Id = "second-account", OrganizationId = "org", Name = "Second",
                CredentialPath = "synthetic", CredentialGeneration = "test" },
            new ProviderAccount { Id = "foreign-account", OrganizationId = "other", Name = "Foreign secret name",
                CredentialPath = "synthetic", CredentialGeneration = "test" });
        foreach (var (id, account, organization, region) in new[]
        {
            ("remote-region", "account", "org", "hel1"), ("second-plan", "second-account", "org", "nbg1"),
            ("foreign-plan", "foreign-account", "other", "nbg1")
        })
            db.ProviderPlans.Add(new ProviderPlan { Id = id, Name = id, NormalizedName = id,
                ProviderAccountId = account, OrganizationId = organization, Region = region, Sku = "cx-test" });
        await db.SaveChangesAsync();
        policy.Profiles["standard"].AllowedPlanIds = ["plan", "remote-region", "second-plan", "foreign-plan"];
        policy.Profiles["standard"].AllowedRegions = ["nbg1", "hel1"];
        policy.Images["qualified"].AllowedRegions = ["nbg1", "hel1"];
        foreach (var account in new[] { "second-account", "foreign-account" })
            policy.Images[account] = new() { Enabled = true, RuntimeQualified = true, QualifiedAt = clock.GetUtcNow().AddMinutes(-1),
                ProviderAccountId = account, ProviderImageId = "123", AllowedRegions = ["nbg1"], ManifestSha256 = new('a', 64) };

        var all = await Service().GetSelectionsAsync("org", null, default);
        Assert.Contains(all.Options, x => x.PlanId == "remote-region");
        Assert.Contains(all.Options, x => x.PlanId == "second-plan");
        Assert.DoesNotContain(all.Options, x => x.ProviderAccountName == "Foreign secret name");
        var fixedChoices = await Service().GetSelectionsAsync("org", workspace.Id, default);
        Assert.Equal(2, fixedChoices.Options.Count);
        Assert.All(fixedChoices.Options, x =>
        {
            Assert.Equal("Test", x.ProviderAccountName);
            Assert.Equal("nbg1", x.Region);
        });
        await AssertCode("workspace_not_found", () => Service().GetSelectionsAsync("other", workspace.Id, default));
        await AssertCode("workspace_not_found", () => Service().GetSelectionsAsync("org", "missing", default));
        foreach (var choice in all.Options.Where(x => x.PlanId is "remote-region" or "second-plan"))
        {
            await AssertCode("workspace_placement_invalid", () => Service().ResizeAsync(workspace.Id, "org", null,
                new(0, choice.ImageId, choice.Profile, choice.PlanId), default));
            await AssertCode("workspace_placement_invalid", () => Service().LaunchEphemeralAsync(workspace.Id, "org",
                Launch() with { ImageId = choice.ImageId, Profile = choice.Profile, PlanId = choice.PlanId }, "actor", default));
        }
        Assert.Single((await Service().GetAsync(workspace.Id, "org", default))!.Vms);
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
    }

    [Fact]
    public async Task WritesRevalidatePolicyAfterDiscoveryIncludingPreviouslyScheduledResize()
    {
        var workspace = await Workspace();
        Assert.NotEmpty((await Service().GetSelectionsAsync("org", workspace.Id, default)).Options);
        var plan = await db.ProviderPlans.SingleAsync(x => x.Id == "plan");
        plan.Enabled = false;
        await db.SaveChangesAsync();
        await AssertCode("workspace_plan_invalid", () => Service().CreateAsync(Create("Second", "second"), "actor", default));
        await AssertCode("workspace_plan_invalid", () => Service().ResizeAsync(workspace.Id, "org", null,
            new(0, "qualified", "standard", "plan"), default));
        await AssertCode("workspace_plan_invalid", () => Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default));
        await AssertCode("workspace_plan_invalid", () => Service().StartAsync(workspace.Id, "org", default));
        await Service().ResizeAsync(workspace.Id, "org", null, new(0, "qualified", "heavy", "heavy-plan"), default);
        policy.Profiles["heavy"].AllowedSkus = [];
        await AssertCode("workspace_plan_invalid", () => Service().StartAsync(workspace.Id, "org", default));
        Assert.Empty((await Service().GetSelectionsAsync("org", workspace.Id, default)).Options);
        Assert.Single(await db.DevelopmentWorkspaceVms.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
    }

    [Fact]
    public async Task DiscoveryDoesNotReserveSpendOrBypassCurrentAdmission()
    {
        var workspace = await Workspace();
        Assert.NotEmpty((await Service().GetSelectionsAsync("org", workspace.Id, default)).Options);
        policy.MaxEstimatedMonthlySpendPerOrganization = 3;
        await AssertCode("workspace_spend_limit", () => Service().CreateAsync(Create("Second", "second"), "actor", default));
        await AssertCode("workspace_spend_limit", () => Service().StartAsync(workspace.Id, "org", default));
        await AssertCode("workspace_spend_limit", () => Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default));
        policy.MaxEstimatedMonthlySpendPerOrganization = 5000;
        await Service().StartAsync(workspace.Id, "org", default);
        policy.MaxVmsPerWorkspace = 1;
        Assert.NotEmpty((await Service().GetSelectionsAsync("org", workspace.Id, default)).Options);
        await AssertCode("workspace_admission_limit", () => Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default));
    }

    [Fact]
    public async Task CreateIsStoppedIdempotentAndDoesNotAllocateOrTouchHostedApps()
    {
        db.Apps.Add(new MinicloudApp { Id = "hosted", OrganizationId = "org", Name = "Hosted", Slug = "hosted" });
        await db.SaveChangesAsync();
        var workspace = await Workspace();
        var again = await Service().CreateAsync(Create(), "actor", default);
        Assert.Equal(workspace.Id, again.Id);
        Assert.Single(again.Vms);
        Assert.Equal("stopped", Primary(again).Phase);
        Assert.Equal(0, Primary(again).Generation);
        Assert.Null(again.ProviderVolumeId);
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
        Assert.False(Primary(again).ComputeReserved);
        Assert.Null((await db.ProviderPlans.SingleAsync(x => x.Id == "plan")).ProvisioningLockedAt);
        await AssertCode("workspace_idempotency_conflict", () => Service().CreateAsync(Create("Changed"), "actor", default));
        Assert.Equal("Hosted", (await db.Apps.SingleAsync()).Name);
        await AssertCode("workspace_not_found", () => Service().StopAsync("hosted", "org", default));
        await AssertCode("workspace_not_found", () => Service().StopAsync("control-plane", "org", default));
    }

    [Fact]
    public async Task ConcurrentDuplicateStartReservesExactlyOneGenerationAndOperation()
    {
        var workspace = await Workspace();
        await using var first = Context(); await using var second = Context();
        var results = await Task.WhenAll(Service(first).StartAsync(workspace.Id, "org", default),
            Service(second).StartAsync(workspace.Id, "org", default));
        Assert.All(results, x => Assert.Equal(1, Primary(x).Generation));
        Assert.Single(await db.DevelopmentWorkspaceOperations.AsNoTracking().ToListAsync());
        Assert.Single(await db.DevelopmentWorkspaceVms.AsNoTracking().Where(x => x.ComputeReserved).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentLaunchesCannotExceedCapAndStoppingStillReservesSpend()
    {
        var workspace = await Workspace();
        policy.MaxVmsPerWorkspace = 1;
        await using var first = Context(); await using var second = Context();
        async Task<bool> TryLaunch(MinicloudDbContext context, string key)
        {
            try { await Service(context).LaunchEphemeralAsync(workspace.Id, "org", Launch(key), "actor", default); return true; }
            catch (DevelopmentWorkspaceException error) { Assert.Equal("workspace_admission_limit", error.Code); return false; }
        }
        var result = await Task.WhenAll(TryLaunch(first, "one"), TryLaunch(second, "two"));
        Assert.Single(result, x => x);
        var current = (await Service().GetAsync(workspace.Id, "org", default))!;
        var ephemeral = current.Vms.Single(x => x.Kind == "ephemeral");
        await Service().StopVmAsync(workspace.Id, "org", ephemeral.Id, 1, default);
        await AssertCode("workspace_admission_limit", () => Service().StartAsync(workspace.Id, "org", default));
        Assert.Single(await db.DevelopmentWorkspaceVms.Where(x => x.ComputeReserved).ToListAsync());
    }

    [Fact]
    public async Task StorageAndProposedComputeAreIncludedInOrgSpendCap()
    {
        policy.MaxEstimatedMonthlySpendPerOrganization = 73;
        var workspace = await Workspace(); // storage = 2, proposed compute = 72.
        await AssertCode("workspace_spend_limit", () => Service().StartAsync(workspace.Id, "org", default));
        Assert.False((await db.DevelopmentWorkspaceVms.SingleAsync()).ComputeReserved);
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
        policy.MaxEstimatedMonthlySpendPerOrganization = 1;
        await AssertCode("workspace_spend_limit", () => Service().CreateAsync(Create("Second", "second"), "actor", default));
        Assert.Single(await db.DevelopmentWorkspaces.ToListAsync());
    }

    [Fact]
    public async Task OrgAndVmBoundariesAreCheckedBeforeMutation()
    {
        var workspace = await Workspace();
        await AssertCode("workspace_not_found", () => Service().StartAsync(workspace.Id, "other", default));
        Assert.Null(await Service().GetAsync(workspace.Id, "other", default));
        Assert.Empty(await Service().ListAsync("other", default));
        await AssertCode("workspace_vm_not_found", () => Service().StartVmAsync(workspace.Id, "org", "foreign", 0, default));
        var second = await Service().CreateAsync(Create("Second", "second"), "actor", default);
        await AssertCode("workspace_vm_not_found", () => Service().StartVmAsync(workspace.Id, "org", Primary(second).Id, 0, default));
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
    }

    [Fact]
    public async Task EphemeralHasIndependentGenerationBranchTasksAndNoStorageOrPrimaryCredentialContract()
    {
        var workspace = await Workspace();
        workspace = await Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default);
        var vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        Assert.Equal(0, Primary(workspace).Generation);
        Assert.Equal(1, vm.Generation);
        Assert.Null(workspace.ProviderVolumeId);
        Assert.StartsWith($"minicloud/{vm.Id}/g", vm.WorkBranch);
        Assert.Null(vm.RepositoryCredentialRef);
        Assert.Equal("base_verification_pending", vm.GitStatus);
        var same = await Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default);
        Assert.Equal(2, same.Vms.Count);
        var another = await Service().LaunchEphemeralAsync(workspace.Id, "org", Launch("another"), "actor", default);
        Assert.Equal(2, another.Vms.Where(x => x.Kind == "ephemeral").Select(x => x.WorkBranch).Distinct().Count());
        await AssertCode("workspace_discard_invalid", () => Service().DiscardAsync(workspace.Id, "org",
            Primary(workspace).Id, new(0, true, true), "actor", default));
        var response = Service().ToResponse(another);
        Assert.Equal(2, response.EstimatedMonthlyStorage);
        Assert.Equal(144, response.EstimatedMonthlyCompute);
        Assert.DoesNotContain("CredentialRef", System.Text.Json.JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task SelectionIsPinnedUntilNextGenerationAndStoppedResizeIsPersisted()
    {
        var workspace = await Workspace();
        workspace = await Service().StartAsync(workspace.Id, "org", default);
        var vm = Primary(workspace);
        var firstOperation = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync();
        await Service().ResizeAsync(workspace.Id, "org", vm.Id, new(1, "qualified", "heavy", "heavy-plan"), default);
        var current = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("standard", Primary(current).Profile);
        Assert.Equal("heavy", Primary(current).NextProfile);
        Assert.Equal(.1m, firstOperation.ComputeHourlyRate);
        var saved = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        saved.Phase = "stopped"; saved.DesiredState = "stopped"; saved.ComputeReserved = false;
        await db.SaveChangesAsync();
        policy.Images["qualified"].ProviderImageId = "image-new";
        workspace = await Service().StartVmAsync(workspace.Id, "org", vm.Id, 1, default);
        vm = Primary(workspace);
        Assert.Equal(2, vm.Generation);
        Assert.Equal("heavy", vm.Profile);
        Assert.Equal("image-new", vm.ProviderImage);
        Assert.Null(vm.NextPlanId);
        var old = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == firstOperation.Id);
        Assert.Equal("image-123", old.ProviderImage);
        Assert.Equal("plan", old.PlanId);
        Assert.NotNull((await db.ProviderPlans.SingleAsync(x => x.Id == "plan")).ProvisioningLockedAt);
        await AssertCode("workspace_generation_stale", () => Service().ResizeAsync(workspace.Id, "org", vm.Id,
            new(1, "qualified", "standard", "plan"), default));
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("qualification")]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("sku")]
    [InlineData("provider")]
    [InlineData("disabled")]
    [InlineData("disabled-account")]
    [InlineData("disabled-image")]
    [InlineData("future-qualification")]
    [InlineData("manifest")]
    [InlineData("provider-image")]
    [InlineData("plan-architecture")]
    [InlineData("profile-region")]
    [InlineData("profile-plan")]
    [InlineData("profile-memory")]
    [InlineData("profile-cpu")]
    [InlineData("profile-rate")]
    public async Task UnsupportedOrUnqualifiedSelectionsFailClosed(string failure)
    {
        switch (failure)
        {
            case "architecture": policy.Images["qualified"].Architecture = "linux/arm64"; break;
            case "qualification": policy.Images["qualified"].RuntimeQualified = false; break;
            case "account": policy.Images["qualified"].ProviderAccountId = "wrong"; break;
            case "region": policy.Images["qualified"].AllowedRegions = ["other"]; break;
            case "sku": policy.Profiles["standard"].AllowedSkus = ["other"]; break;
            case "provider": (await db.ProviderAccounts.SingleAsync()).ProviderType = CloudProvider.DigitalOcean; break;
            case "disabled": (await db.ProviderPlans.SingleAsync(x => x.Id == "plan")).Enabled = false; break;
            case "disabled-account": (await db.ProviderAccounts.SingleAsync()).Enabled = false; break;
            case "disabled-image": policy.Images["qualified"].Enabled = false; break;
            case "future-qualification": policy.Images["qualified"].QualifiedAt = clock.GetUtcNow().AddHours(1); break;
            case "manifest": policy.Images["qualified"].ManifestSha256 = "unverified"; break;
            case "provider-image": policy.Images["qualified"].ProviderImageId = ""; break;
            case "plan-architecture": (await db.ProviderPlans.SingleAsync(x => x.Id == "plan")).BuildPlatform = "linux/arm64"; break;
            case "profile-region": policy.Profiles["standard"].AllowedRegions = ["other"]; break;
            case "profile-plan": policy.Profiles["standard"].AllowedPlanIds = []; break;
            case "profile-memory": policy.Profiles["standard"].MemoryGiB = 4; break;
            case "profile-cpu": policy.Profiles["standard"].CpuCount = 0; break;
            case "profile-rate": policy.Profiles["standard"].ComputeHourlyRate = 0; break;
        }
        await db.SaveChangesAsync();
        var choices = await Service().GetSelectionsAsync("org", null, default);
        Assert.True(choices.Available);
        Assert.DoesNotContain(choices.Options, x => x.Profile == "standard" && x.PlanId == "plan");
        await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() => Workspace());
        Assert.Empty(await db.DevelopmentWorkspaces.ToListAsync());
    }

    [Fact]
    public async Task GitPolicyRequiresExplicitRemoteBaseCommitPushAndIgnoredDataAcknowledgement()
    {
        var workspace = await Workspace();
        foreach (var request in new[] { Launch() with { PushAuthorized = false }, Launch() with { AcknowledgeIgnoredDataLoss = false },
            Launch() with { BaseCommit = "main" }, Launch() with { Repository = "file:///primary/workspace" },
            Launch() with { Repository = "https://unapproved.example.test/team/repo.git" },
            Launch() with { Repository = "https://user:token@git.example.test/repo" },
            Launch() with { RepositoryCredentialRef = "primary-ssh-key" } })
            await Assert.ThrowsAsync<DevelopmentWorkspaceException>(() =>
                Service().LaunchEphemeralAsync(workspace.Id, "org", request, "actor", default));
        Assert.Single((await Service().GetAsync(workspace.Id, "org", default))!.Vms);
    }

    [Fact]
    public async Task RepositoryGitHubAuthorityRequiresExplicitConnectedOrganizationSelectionWithoutTokenExpiry()
    {
        var workspace = await Workspace();
        var repository = policy.Repositories.Single();
        repository.Repository = "https://github.com/fixture-owner/private-repo.git";
        repository.GitHubConnectionId = "github";
        db.GitHubAppConnections.Add(new()
        {
            Id = "github", OrganizationId = "other", Status = "connected", AppId = 42, InstallationId = 900,
            LastVerifiedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var request = Launch() with { Repository = repository.Repository };
        await AssertCode("workspace_git_credential_invalid", () =>
            Service().LaunchEphemeralAsync(workspace.Id, "org", request, "actor", default));
        var connection = await db.GitHubAppConnections.SingleAsync();
        connection.OrganizationId = "org"; connection.Status = "disconnected";
        await db.SaveChangesAsync();
        await AssertCode("workspace_git_credential_invalid", () =>
            Service().LaunchEphemeralAsync(workspace.Id, "org", request, "actor", default));
        connection = await db.GitHubAppConnections.SingleAsync();
        connection.Status = "connected";
        await db.SaveChangesAsync();
        var selected = System.Text.Json.JsonSerializer.Serialize(new StoredSecretReference("github-app", "github", repository.Repository));
        await AssertCode("workspace_git_credential_invalid", () => Service().LaunchEphemeralAsync(workspace.Id, "org",
            request with { RepositoryCredentialRef = selected.Replace("github\"", "foreign\"", StringComparison.Ordinal) }, "actor", default));
        var launched = await Service().LaunchEphemeralAsync(workspace.Id, "org", request, "actor", default);
        Assert.Equal(selected, launched.Vms.Single(x => x.Kind == "ephemeral").RepositoryCredentialRef);
        Assert.Null(repository.CredentialExpiresAt);
    }

    [Fact]
    public async Task RepositoryCredentialUsesExistingIntegrationNamespaceAndExactApprovedReference()
    {
        var workspace = await Workspace();
        var repository = policy.Repositories.Single();
        repository.Credential = new("openbao", OpenBaoSecretStore.IntegrationPathFor(
            "workspace-tests", "org", "workspace-repository-test", "generation"), "1");
        repository.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(30);
        var selected = System.Text.Json.JsonSerializer.Serialize(repository.Credential);
        var other = repository.Credential with { Path = OpenBaoSecretStore.IntegrationPathFor(
            "workspace-tests", "org", "workspace-repository-other", "generation") };
        await AssertCode("workspace_git_credential_invalid", () => Service().LaunchEphemeralAsync(workspace.Id, "org",
            Launch() with { RepositoryCredentialRef = System.Text.Json.JsonSerializer.Serialize(other) }, "actor", default));
        workspace = await Service().LaunchEphemeralAsync(workspace.Id, "org",
            Launch() with { RepositoryCredentialRef = selected }, "actor", default);
        Assert.Equal(selected, workspace.Vms.Single(x => x.Kind == "ephemeral").RepositoryCredentialRef);
        Assert.Null(Primary(workspace).RepositoryCredentialRef);
        Assert.DoesNotContain("workspace-repository-test", System.Text.Json.JsonSerializer.Serialize(Service().ToResponse(workspace)));
    }

    [Theory]
    [InlineData("env/test/orgs/other/integrations/workspace-repository-test/credentials/generation")]
    [InlineData("env/test/orgs/org/integrations/github/credentials/generation")]
    [InlineData("env/test/orgs/org/integrations/workspace-runtime-test/credentials/generation")]
    [InlineData("env/test/orgs/org/integrations/workspace-repository-/credentials/generation")]
    [InlineData("env/test/orgs/org/integrations/workspace-repository-test/credentials/generation/extra")]
    [InlineData("env/test/orgs/org/workspaces/repositories/test/generation")]
    public async Task RepositoryCredentialRejectsForeignOrNonRepositoryIntegrationPaths(string path)
    {
        var workspace = await Workspace();
        var repository = policy.Repositories.Single();
        repository.Credential = new("openbao", path, "1");
        repository.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(30);
        await AssertCode("workspace_git_credential_invalid", () =>
            Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default));
        Assert.Single((await Service().GetAsync(workspace.Id, "org", default))!.Vms);
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public async Task RepositoryCredentialRejectsExpiredOrUnboundedLifetime(int minutes)
    {
        var workspace = await Workspace();
        var repository = policy.Repositories.Single();
        repository.Credential = new("openbao", OpenBaoSecretStore.IntegrationPathFor(
            "workspace-tests", "org", "workspace-repository-test", "generation"), "1");
        repository.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(minutes);
        await AssertCode("workspace_git_credential_invalid", () =>
            Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default));
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
    }

    [Fact]
    public async Task StaleTaskAndDiscardRequestsCannotAffectNewGeneration()
    {
        var workspace = await Service().LaunchEphemeralAsync((await Workspace()).Id, "org", Launch(), "actor", default);
        var vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        await Ready(vm.Id);
        await Service().RenewTaskLeaseAsync(workspace.Id, "org", vm.Id, 1, "task", default);
        await AssertCode("workspace_generation_stale", () => Service().RenewTaskLeaseAsync(workspace.Id, "org", vm.Id, 0, "task", default));
        await AssertCode("workspace_generation_stale", () => Service().CompleteTaskAsync(workspace.Id, "org", vm.Id, 0, "task", false, default));
        await AssertCode("workspace_generation_stale", () => Service().DiscardAsync(workspace.Id, "org", vm.Id, new(0, true, true), "actor", default));
        var lease = await Service().CompleteTaskAsync(workspace.Id, "org", vm.Id, 1, "task", false, default);
        Assert.Equal("completed", lease.Status);
        await AssertCode("workspace_task_completed", () => Service().RenewTaskLeaseAsync(workspace.Id, "org", vm.Id, 1, "task", default));
        var detail = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("completed", Service().ToVmResponse(detail.Vms.Single(x => x.Id == vm.Id)).TaskLeases!.Single().Status);
    }

    [Fact]
    public async Task ExpiredTaskAndMissingHeartbeatBlockIdleStopAndCancellationFencesWorker()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId);
        await Service().RenewTaskLeaseAsync(workspace.Id, "org", vmId, 1, "task", default);
        clock.Advance(TimeSpan.FromMinutes(40));
        await Ready(vmId, idle: true);
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        Assert.False(await Service().IsIdleAsync(vm, default));
        await Service().CompleteTaskAsync(workspace.Id, "org", vmId, 1, "task", false, default);
        await Ready(vmId, idle: true);
        vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        vm.LastHeartbeatAt = null;
        await db.SaveChangesAsync();
        Assert.False(await Service().IsIdleAsync(vm, default));
        await Ready(vmId, idle: true);
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, clock.GetUtcNow().AddMinutes(1), default);
        workspace = await Service().CancelIdleStopVmAsync(workspace.Id, "org", vmId, 1, default);
        Assert.Equal("ready", Primary(workspace).Phase);
        Assert.Null(Primary(workspace).ShutdownDeadline);
        Assert.Contains(await db.DevelopmentWorkspaceOperations.ToListAsync(), x => x.Kind == "stop" && x.Status == "cancelled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleGraceKeepsAdmissionAndUsefulActivityOrTaskRenewalCancelsIt(bool renewTask)
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        var deadline = clock.GetUtcNow().AddMinutes(1);
        workspace = await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, deadline, default);
        Assert.Equal("running", Primary(workspace).DesiredState);
        Assert.Equal("ready", Primary(workspace).Phase);
        Assert.Equal(deadline, Primary(workspace).ShutdownDeadline);
        Assert.Null(await Service().ClaimOperationAsync("worker", default));
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, deadline.AddMinutes(1), default);
        Assert.Single(await db.DevelopmentWorkspaceOperations.Where(x => x.Kind == "stop").ToListAsync());
        if (renewTask)
            await Service().RenewTaskLeaseAsync(workspace.Id, "org", vmId, 1, "task", default);
        else
            await Service().ActivityAsync(workspace.Id, "org", vmId, 1, default);
        workspace = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("running", Primary(workspace).DesiredState);
        Assert.Equal("ready", Primary(workspace).Phase);
        Assert.Equal(1, Primary(workspace).Generation);
        Assert.Null(Primary(workspace).ShutdownDeadline);
        Assert.Null(Primary(workspace).OperationId);
        Assert.Equal("cancelled", (await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Kind == "stop")).Status);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await Service().ClaimOperationAsync("worker", default));
    }

    [Fact]
    public async Task IdleClaimAtomicallyFencesAdmissionAndLateActivityRenewalOrCancellationCannotUndoIt()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, clock.GetUtcNow().AddMinutes(1), default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var claim = (await Service().ClaimOperationAsync("worker", default))!;
        Assert.Equal("stop", claim.Kind);
        Assert.Equal(1, claim.Attempts);
        workspace = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("stopped", Primary(workspace).DesiredState);
        Assert.Equal("draining", Primary(workspace).Phase);
        await AssertCode("workspace_not_ready", () => Service().ActivityAsync(workspace.Id, "org", vmId, 1, default));
        await AssertCode("workspace_not_ready", () => Service().RenewTaskLeaseAsync(workspace.Id, "org", vmId, 1, "late", default));
        await AssertCode("workspace_shutdown_committed", () => Service().CancelIdleStopVmAsync(workspace.Id, "org", vmId, 1, default));
        clock.Advance(TimeSpan.FromSeconds(121));
        await AssertCode("workspace_shutdown_committed", () => Service().CancelIdleStopVmAsync(workspace.Id, "org", vmId, 1, default));
        Assert.Empty(await db.DevelopmentWorkspaceTaskLeases.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentIdleClaimAndUsefulActivityHaveOnlyOneWinner(bool renewTask)
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, clock.GetUtcNow(), default);
        await using var activityDb = Context();
        await using var workerDb = Context();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Activity()
        {
            await start.Task;
            try
            {
                if (renewTask)
                    await Service(activityDb).RenewTaskLeaseAsync(workspace.Id, "org", vmId, 1, "racing-task", default);
                else
                    await Service(activityDb).ActivityAsync(workspace.Id, "org", vmId, 1, default);
                return true;
            }
            catch (DevelopmentWorkspaceException error) { Assert.Equal("workspace_not_ready", error.Code); return false; }
        }
        async Task<DevelopmentWorkspaceOperation?> Claim()
        {
            await start.Task;
            return await Service(workerDb).ClaimOperationAsync("worker", default);
        }
        var activity = Activity();
        var claim = Claim();
        start.SetResult();
        await Task.WhenAll(activity, claim);
        var active = await activity;
        var claimed = await claim;
        workspace = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal(1, Primary(workspace).Generation);
        Assert.Equal(claimed is null, active);
        Assert.Equal(active ? "ready" : "draining", Primary(workspace).Phase);
        Assert.Equal(active ? "running" : "stopped", Primary(workspace).DesiredState);
        Assert.Equal(renewTask && active ? 1 : 0, await db.DevelopmentWorkspaceTaskLeases.CountAsync());
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("task")]
    [InlineData("heartbeat")]
    public async Task IdleClaimRechecksCurrentActivityTasksAndHeartbeatAtDeadline(string blocker)
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, clock.GetUtcNow().AddMinutes(1), default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        if (blocker == "activity") vm.LastActivityAt = clock.GetUtcNow();
        if (blocker == "heartbeat") vm.LastHeartbeatAt = null;
        if (blocker == "task")
            db.DevelopmentWorkspaceTaskLeases.Add(new() { Id = "reported-task", WorkspaceId = workspace.Id,
                VmId = vmId, TaskId = "reported", Generation = 1, ExpiresAt = clock.GetUtcNow().AddMinutes(-1),
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync();
        Assert.Null(await Service().ClaimOperationAsync("worker", default));
        workspace = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("running", Primary(workspace).DesiredState);
        Assert.Equal("ready", Primary(workspace).Phase);
        var stop = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Kind == "stop");
        Assert.Equal(blocker == "heartbeat" ? "pending" : "cancelled", stop.Status);
        Assert.Equal(0, stop.Attempts);
    }

    [Fact]
    public async Task ManualStopDuringIdleGraceImmediatelyFencesAdmissionAndSupersedesDeadline()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        await Service().ScheduleIdleStopAsync(workspace.Id, "org", vmId, 1, clock.GetUtcNow().AddMinutes(1), default);
        workspace = await Service().StopAsync(workspace.Id, "org", default);
        Assert.Equal("stopped", Primary(workspace).DesiredState);
        Assert.Equal("draining", Primary(workspace).Phase);
        Assert.Equal("manual", Primary(workspace).ShutdownReason);
        Assert.Equal(clock.GetUtcNow(), Primary(workspace).ShutdownDeadline);
        Assert.Equal("stop", (await Service().ClaimOperationAsync("worker", default))!.Kind);
        await AssertCode("workspace_not_ready", () => Service().ActivityAsync(workspace.Id, "org", vmId, 1, default));
    }

    [Theory]
    [InlineData("draining", "pending", "pending", null)]
    [InlineData("draining", "wait-for-tasks", "running", null)]
    [InlineData("awaiting_git_save", "wait-for-save", "pending", "workspace_git_save_required")]
    public async Task RecoveryActivityRecordsTimestampWithoutReopeningAdmissionOrCancellingStop(
        string phase, string step, string status, string? error)
    {
        var (workspace, vm, operation, _) = await RecoveryVm();
        vm.Phase = phase; vm.ErrorCode = error; operation.Step = step; operation.Status = status;
        if (phase == "awaiting_git_save") vm.QuiescedGeneration = 0;
        await db.SaveChangesAsync();
        var id = operation.Id;
        var updated = await Service().ActivityAsync(workspace.Id, "org", vm.Id, 1, default);
        vm = Primary(updated);
        Assert.Equal(clock.GetUtcNow(), vm.LastActivityAt);
        Assert.Equal("stopped", vm.DesiredState);
        Assert.Equal(phase, vm.Phase);
        Assert.Equal(id, vm.OperationId);
        Assert.Equal(1, vm.Generation);
        Assert.Equal("manual", vm.ShutdownReason);
        Assert.NotNull(vm.ShutdownDeadline);
        Assert.Equal(status, (await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == id)).Status);
        await AssertCode("workspace_not_ready", () =>
            Service().RenewTaskLeaseAsync(workspace.Id, "org", vm.Id, 1, "new-task", default));
    }

    [Theory]
    [InlineData("quiesced")]
    [InlineData("gateway-not-ready")]
    [InlineData("heartbeat-missing")]
    [InlineData("route-expired")]
    [InlineData("route-revoked")]
    [InlineData("route-stale-generation")]
    [InlineData("operation-stale-generation")]
    [InlineData("operation-completed")]
    [InlineData("unsafe-step")]
    [InlineData("wrong-git-error")]
    public async Task RecoveryActivityRejectsAnythingOutsideCurrentLiveRecoverableStop(string failure)
    {
        var (workspace, vm, operation, route) = await RecoveryVm();
        switch (failure)
        {
            case "quiesced": vm.QuiescedGeneration = vm.Generation; break;
            case "gateway-not-ready": vm.GatewayReady = false; break;
            case "heartbeat-missing": vm.LastHeartbeatAt = null; break;
            case "route-expired": route.ExpiresAt = clock.GetUtcNow(); break;
            case "route-revoked": route.RevokedAt = clock.GetUtcNow(); break;
            case "route-stale-generation": route.Generation = 0; break;
            case "operation-stale-generation": vm.Generation++; break;
            case "operation-completed": operation.Status = "completed"; break;
            case "unsafe-step": operation.Step = "delete-compute"; break;
            case "wrong-git-error":
                vm.Phase = "awaiting_git_save"; operation.Step = "wait-for-save"; vm.ErrorCode = "workspace_runtime_unreachable"; break;
        }
        await db.SaveChangesAsync();
        await AssertCode("workspace_not_ready", () =>
            Service().ActivityAsync(workspace.Id, "org", vm.Id, vm.Generation, default));
        var current = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal(clock.GetUtcNow().AddMinutes(-10), Primary(current).LastActivityAt);
        Assert.Equal("stopped", Primary(current).DesiredState);
        Assert.Equal(operation.Id, Primary(current).OperationId);
    }

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation,
        DevelopmentWorkspaceGatewayRoute Route)> RecoveryVm()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId);
        await Service().StopAsync(workspace.Id, "org", default);
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        vm.GatewayReady = true; vm.LastActivityAt = clock.GetUtcNow().AddMinutes(-10);
        var route = new DevelopmentWorkspaceGatewayRoute { Id = "recovery-route", OrganizationId = "org",
            WorkspaceId = workspace.Id, VmId = vm.Id, Generation = vm.Generation,
            LoopbackPort = 32001, RegisteredAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddHours(1) };
        db.DevelopmentWorkspaceGatewayRoutes.Add(route);
        await db.SaveChangesAsync();
        return (workspace, vm, operation, route);
    }

    [Fact]
    public async Task ExpiredOperationTokenCannotCommitAfterReclaimAndDiscardStillNeedsQuiescence()
    {
        var workspace = await Service().LaunchEphemeralAsync((await Workspace()).Id, "org", Launch(), "actor", default);
        var vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        var claim = (await Service().ClaimOperationAsync("worker-one", default))!;
        clock.Advance(TimeSpan.FromSeconds(121));
        var replacement = (await Service().ClaimOperationAsync("worker-two", default))!;
        Assert.NotEqual(claim.LeaseToken, replacement.LeaseToken);
        await AssertCode("workspace_operation_stale", () => Service().RenewOperationLeaseAsync(claim.Id, claim.LeaseToken!, default));
        await Service().UpdateClaimedOperationAsync(replacement.Id, replacement.LeaseToken!, (operation, current, _) =>
        {
            operation.Status = "completed"; current.Phase = "ready";
        }, default);
        workspace = await Service().DiscardAsync(workspace.Id, "org", vm.Id, new(1, true, true), "actor", default);
        vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        var stop = (await Service().ClaimOperationAsync("worker-two", default))!;
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
        await Service().UpdateClaimedOperationAsync(stop.Id, stop.LeaseToken!, (operation, current, _) =>
        {
            current.LastHeartbeatAt = clock.GetUtcNow(); current.QuiescedAt = clock.GetUtcNow();
            current.QuiescedGeneration = current.Generation; current.QuiescedOperationId = operation.Id;
            current.QuiescenceReceipt = "worker-verified";
        }, default);
        vm = (await Service().GetAsync(workspace.Id, "org", default))!.Vms.Single(x => x.Id == vm.Id);
        Assert.True(await Service().CanDeleteComputeAsync(vm, stop, default));
        vm.DiscardGeneration = 0;
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedBlockedStopDoesNotStarveOtherVmAndRemainsReclaimable(bool advanceClock)
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var primary = Primary(workspace);
        await Ready(primary.Id);
        workspace = await Service().LaunchEphemeralAsync(workspace.Id, "org", Launch(), "actor", default);
        var ephemeral = workspace.Vms.Single(x => x.Kind == "ephemeral");
        await Ready(ephemeral.Id);
        await Service().StopVmAsync(workspace.Id, "org", ephemeral.Id, 1, default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Service().StopVmAsync(workspace.Id, "org", primary.Id, 1, default);
        if (advanceClock) clock.Advance(TimeSpan.FromSeconds(1));
        var blocked = (await Service().ClaimOperationAsync("worker", default))!;
        Assert.Equal(ephemeral.Id, blocked.VmId);
        await Service().UpdateClaimedOperationAsync(blocked.Id, blocked.LeaseToken!, (operation, vm, _) =>
        {
            operation.Status = "pending"; operation.Step = "wait-for-tasks";
            operation.LeaseToken = null; operation.LeaseOwner = null; operation.LeaseExpiresAt = null;
            vm.Phase = "blocked"; vm.ErrorCode = "workspace_reconciliation_required";
        }, default);
        if (advanceClock) clock.Advance(TimeSpan.FromSeconds(1));

        var independent = (await Service().ClaimOperationAsync("worker", default))!;
        Assert.Equal(primary.Id, independent.VmId);
        var retry = (await Service().ClaimOperationAsync("another-worker", default))!;
        Assert.Equal(blocked.Id, retry.Id);
        Assert.NotEqual(blocked.LeaseToken, retry.LeaseToken);
    }

    [Fact]
    public async Task GitSaveMustBeAfterQuiescenceAndMatchGenerationOperationBranchAndHead()
    {
        var workspace = await Service().LaunchEphemeralAsync((await Workspace()).Id, "org", Launch(), "actor", default);
        var vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        await Ready(vm.Id);
        workspace = await Service().StopVmAsync(workspace.Id, "org", vm.Id, 1, default);
        var stop = (await Service().ClaimOperationAsync("worker", default))!;
        vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        vm.QuiescedGeneration = 1; vm.QuiescedOperationId = stop.Id; vm.QuiescedAt = clock.GetUtcNow();
        vm.LastHeartbeatAt = clock.GetUtcNow(); vm.QuiescenceReceipt = "verified";
        vm.GitSavedGeneration = 1; vm.GitSavedOperationId = stop.Id; vm.GitSavedAt = vm.QuiescedAt;
        vm.GitSaveReceipt = "remote-verified"; vm.FinalHead = new('c', 40); vm.VerifiedRemoteCommit = vm.FinalHead;
        vm.VerifiedRemoteRef = $"refs/heads/{vm.WorkBranch}"; vm.GitStatus = "saved";
        Assert.True(await Service().CanDeleteComputeAsync(vm, stop, default));
        vm.GitSavedAt = vm.QuiescedAt.Value.AddSeconds(-1);
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
        vm.GitSavedAt = vm.QuiescedAt; vm.VerifiedRemoteCommit = new('d', 40);
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
        vm.VerifiedRemoteCommit = vm.FinalHead; vm.GitSavedGeneration = 0;
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
        vm.GitSavedGeneration = 1; vm.LastHeartbeatAt = null;
        Assert.False(await Service().CanDeleteComputeAsync(vm, stop, default));
    }

    private const string OriginalGitBase = "a1183a9d0b7697213e3f13af040dd2a66ae3241a";
    private const string PublishedGitHead = "b89a48cd27b1b7a423c5c9749316017ad3192eeb";

    private async Task<(string Workspace, string Vm, string Stop)> StoppedSavedEphemeral()
    {
        var workspace = await Service().LaunchEphemeralAsync((await Workspace()).Id, "org",
            Launch() with { BaseCommit = OriginalGitBase }, "actor", default);
        var vm = workspace.Vms.Single(x => x.Kind == "ephemeral");
        await Ready(vm.Id);
        await Service().StopVmAsync(workspace.Id, "org", vm.Id, 1, default);
        var stop = (await Service().ClaimOperationAsync("worker", default))!;
        await Service().UpdateClaimedOperationAsync(stop.Id, stop.LeaseToken!, (operation, current, _) =>
        {
            var receipt = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, type = "GitSaved", kind = "ephemeral", vmId = current.Id,
                generation = current.Generation, operationId = operation.Id, receiptId = new string('a', 64),
                repositories = new[] { new { path = "/workspace/repos/project", origin = current.Repository,
                    baseSha = current.BaseCommit, branch = current.WorkBranch, headSha = PublishedGitHead,
                    remoteRef = "refs/heads/" + current.WorkBranch, remoteSha = PublishedGitHead } }
            });
            current.Phase = "stopped"; current.DesiredState = "stopped"; current.ComputeReserved = false;
            current.QuiescedGeneration = current.Generation; current.QuiescedOperationId = operation.Id;
            current.QuiescedAt = clock.GetUtcNow(); current.QuiescenceReceipt = receipt;
            current.GitSavedGeneration = current.Generation; current.GitSavedOperationId = operation.Id;
            current.GitSavedAt = current.QuiescedAt; current.GitSaveReceipt = receipt;
            current.FinalHead = PublishedGitHead; current.VerifiedRemoteCommit = PublishedGitHead;
            current.VerifiedRemoteRef = "refs/heads/" + current.WorkBranch; current.GitStatus = "saved";
            operation.Status = "completed"; operation.Step = "stopped";
            operation.LeaseToken = null; operation.LeaseOwner = null; operation.LeaseExpiresAt = null;
            operation.ProviderEvidenceJson = JsonSerializer.Serialize(new
                { receiptId = new string('a', 64), repositoryCredentialRevoked = true, deleteCommitted = true });
        }, default);
        return (workspace.Id, vm.Id, stop.Id);
    }

    [Fact]
    public async Task RecreateCapturesVerifiedSavedHeadBeforeResettingPreviousGeneration()
    {
        var (workspaceId, vmId, stopId) = await StoppedSavedEphemeral();
        var workspace = await Service().StartVmAsync(workspaceId, "org", vmId, 1, default);
        var vm = workspace.Vms.Single(x => x.Id == vmId);
        Assert.Equal(PublishedGitHead, vm.BaseCommit);
        Assert.Equal("refs/heads/main", vm.BaseRef);
        Assert.Equal($"minicloud/{vmId}/g2", vm.WorkBranch);
        Assert.Equal(2, vm.Generation);
        Assert.Null(vm.FinalHead);
        Assert.Null(vm.GitSaveReceipt);
        var operation = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == vm.OperationId);
        Assert.Equal("git-recovery-pending", operation.Step);
        var source = JsonNode.Parse(operation.ProviderEvidenceJson!)!["gitRecovery"]!;
        Assert.Equal(PublishedGitHead, source["commit"]!.GetValue<string>());
        Assert.Equal($"refs/heads/minicloud/{vmId}/g1", source["remoteRef"]!.GetValue<string>());
        Assert.Equal(1, source["generation"]!.GetValue<long>());
        Assert.Equal(stopId, source["operationId"]!.GetValue<string>());
        Assert.Equal(vm.Repository, source["repository"]!.GetValue<string>());
        var stopped = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == stopId);
        Assert.Equal("completed", stopped.Status);
    }

    [Theory]
    [InlineData("unsaved")]
    [InlineData("missing-receipt")]
    [InlineData("unverified")]
    [InlineData("generation")]
    [InlineData("operation")]
    [InlineData("old-save")]
    [InlineData("remote-retarget")]
    [InlineData("default-branch")]
    [InlineData("repository-retarget")]
    [InlineData("receipt-head")]
    [InlineData("receipt-generation")]
    [InlineData("receipt-source")]
    [InlineData("malformed-receipt")]
    [InlineData("unfinished-stop")]
    public async Task RecreateRejectsUnverifiedOrRetargetedSavedSourceWithoutErasingEvidence(string failure)
    {
        var (workspaceId, vmId, stopId) = await StoppedSavedEphemeral();
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        var stop = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == stopId);
        var receipt = JsonNode.Parse(vm.GitSaveReceipt!)!;
        var repository = receipt["repositories"]![0]!;
        switch (failure)
        {
            case "unsaved": vm.GitStatus = "awaiting_save"; break;
            case "missing-receipt": vm.GitSaveReceipt = null; break;
            case "unverified": vm.VerifiedRemoteCommit = new('d', 40); break;
            case "generation": vm.GitSavedGeneration = 0; break;
            case "operation": vm.GitSavedOperationId = "other-operation"; break;
            case "old-save": vm.GitSavedAt = vm.QuiescedAt!.Value.AddSeconds(-1); break;
            case "remote-retarget": vm.VerifiedRemoteRef = "refs/heads/another"; break;
            case "default-branch":
                vm.WorkBranch = "main"; vm.VerifiedRemoteRef = "refs/heads/main";
                repository["branch"] = "main"; repository["remoteRef"] = "refs/heads/main"; break;
            case "repository-retarget":
                vm.Repository = "https://git.example.test/team/other.git";
                policy.Repositories.Add(new() { OrganizationId = "org", Repository = vm.Repository,
                    AllowedBaseRefs = ["refs/heads/main"], AllowPush = true }); break;
            case "receipt-head": repository["headSha"] = new string('d', 40); break;
            case "receipt-generation": receipt["generation"] = 0; break;
            case "receipt-source": repository["remoteRef"] = "refs/heads/another"; break;
            case "malformed-receipt": receipt = new JsonObject(); break;
            case "unfinished-stop": stop.Status = "pending"; break;
        }
        if (failure != "missing-receipt")
            vm.GitSaveReceipt = vm.QuiescenceReceipt = receipt.ToJsonString();
        var evidence = vm.GitSaveReceipt;
        await db.SaveChangesAsync();

        await AssertCode("workspace_git_recovery_invalid",
            () => Service().StartVmAsync(workspaceId, "org", vmId, 1, default));

        vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == vmId);
        Assert.Equal(1, vm.Generation);
        Assert.Equal(OriginalGitBase, vm.BaseCommit);
        Assert.Equal(evidence, vm.GitSaveReceipt);
        Assert.Equal(PublishedGitHead, vm.FinalHead);
        Assert.False(vm.ComputeReserved);
        Assert.Equal(2, await db.DevelopmentWorkspaceOperations.CountAsync());
    }

    [Fact]
    public async Task RecreateAfterExplicitVerifiedDiscardKeepsSelectedBaseWithoutInventingSavedHead()
    {
        var (workspaceId, vmId, stopId) = await StoppedSavedEphemeral();
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vmId);
        var receipt = JsonNode.Parse(vm.QuiescenceReceipt!)!;
        receipt["type"] = "DiscardAuthorized";
        vm.QuiescenceReceipt = receipt.ToJsonString();
        vm.GitSaveReceipt = null; vm.GitSavedAt = null; vm.GitSavedGeneration = null; vm.GitSavedOperationId = null;
        vm.FinalHead = null; vm.VerifiedRemoteCommit = null; vm.VerifiedRemoteRef = null;
        vm.GitStatus = "awaiting_save";
        vm.DiscardGeneration = 1; vm.DiscardOperationId = stopId;
        vm.DiscardRequestedBy = "actor"; vm.DiscardRequestedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();

        var workspace = await Service().StartVmAsync(workspaceId, "org", vmId, 1, default);
        vm = workspace.Vms.Single(x => x.Id == vmId);
        Assert.Equal(OriginalGitBase, vm.BaseCommit);
        Assert.Equal(2, vm.Generation);
    }

    [Fact]
    public async Task StartDuringDrainQueuesIntentWithoutReplacementOrGenerationChange()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId);
        await Service().StopAsync(workspace.Id, "org", default);
        workspace = await Service().StartVmAsync(workspace.Id, "org", vmId, 1, default);
        Assert.True(Primary(workspace).StartAfterStop);
        Assert.Equal("draining", Primary(workspace).Phase);
        Assert.Equal("stopped", Primary(workspace).DesiredState);
        Assert.Equal(1, Primary(workspace).Generation);
        Assert.Equal(2, await db.DevelopmentWorkspaceOperations.CountAsync());
    }

    [Fact]
    public async Task DefaultDisabledAndIncompletePoliciesCannotAdmitResources()
    {
        Assert.False(new DevelopmentWorkspaceOptions().Enabled);
        policy.Enabled = false;
        await AssertCode("workspaces_disabled", () => Workspace());
        Assert.Empty(await db.DevelopmentWorkspaces.ToListAsync());
        policy.Enabled = true; policy.MaxEstimatedMonthlySpendPerOrganization = 0;
        await AssertCode("workspace_policy_invalid", () => Workspace());
    }

    [Fact]
    public async Task ClaimsContinueWhileFencedButRevokedParticipantCannotClaim()
    {
        await Service().StartAsync((await Workspace()).Id, "org", default);
        var participant = MaintenanceParticipant.CreateLocal("worker", clock);
        var service = new DevelopmentWorkspaceService(db, new IdGenerator(), clock, Options.Create(policy), participant);
        var claim = (await service.ClaimOperationAsync("worker", default))!;
        await service.UpdateClaimedOperationAsync(claim.Id, claim.LeaseToken!, Release, default);
        var authority = await db.MaintenanceAuthorities.SingleAsync();
        authority.State = MaintenanceAuthorityStates.Fenced;
        await db.SaveChangesAsync();
        var fenced = (await service.ClaimOperationAsync("worker", default))!;
        Assert.NotNull(fenced);
        await service.UpdateClaimedOperationAsync(fenced.Id, fenced.LeaseToken!, Release, default);
        authority = await db.MaintenanceAuthorities.SingleAsync();
        authority.Epoch++;
        await db.SaveChangesAsync();
        Assert.Null(await service.ClaimOperationAsync("worker", default));

        static void Release(DevelopmentWorkspaceOperation operation, DevelopmentWorkspaceVm vm, DevelopmentWorkspace workspace)
        {
            operation.Status = "pending"; operation.LeaseToken = null;
            operation.LeaseOwner = null; operation.LeaseExpiresAt = null;
        }
    }

    [Fact]
    public async Task AutomaticIdleStopCannotEnqueueWhileMaintenanceIsFenced()
    {
        var workspace = await Service().StartAsync((await Workspace()).Id, "org", default);
        var vmId = Primary(workspace).Id;
        await Ready(vmId, idle: true);
        var authority = await db.MaintenanceAuthorities.SingleAsync();
        authority.State = MaintenanceAuthorityStates.Fenced;
        await db.SaveChangesAsync();
        await AssertCode("workspace_maintenance_fenced", () => Service().ScheduleIdleStopAsync(
            workspace.Id, "org", vmId, 1, clock.GetUtcNow().AddMinutes(1), default));
        Assert.Empty(await db.DevelopmentWorkspaceOperations.Where(x => x.Kind == "stop").ToListAsync());
        workspace = (await Service().GetAsync(workspace.Id, "org", default))!;
        Assert.Equal("running", Primary(workspace).DesiredState);
        Assert.Equal("ready", Primary(workspace).Phase);
        Assert.Null(Primary(workspace).ShutdownDeadline);
    }

    private async Task Ready(string id, bool idle = false)
    {
        db.ChangeTracker.Clear();
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == id);
        vm.Phase = "ready"; vm.LastHeartbeatAt = clock.GetUtcNow();
        vm.LastActivityAt = clock.GetUtcNow().AddMinutes(idle ? -31 : 0);
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        operation.Status = "completed";
        await db.SaveChangesAsync();
    }

    private static async Task AssertCode(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DevelopmentWorkspaceException>(action)).Code);
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
