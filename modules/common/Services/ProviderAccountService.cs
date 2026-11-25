using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Selfrun.Common.Contracts;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Extensibility;

namespace Selfrun.Common.Services;

public sealed class ProviderAccountService(SelfrunDbContext db, ISecretStore secrets,
    ProviderAccountClientFactory clients, IIdGenerator ids, TimeProvider clock, IOptions<OpenBaoSecretStoreOptions> options,
    IVpsProviderRegistry? registry = null)
{
    private static readonly IVpsProviderRegistry DefaultRegistry = VpsProviderRegistry.CreateDefault();
    private readonly IVpsProviderRegistry _registry = registry ?? DefaultRegistry;

    public async Task<ProviderAccount> CreateAsync(CreateProviderRequest request, CancellationToken ct)
    {
        ValidateName(request.Name);
        if (!_registry.TryParseBuiltIn(request.ProviderType, out var type))
            throw new ProviderCatalogException("provider_type_unsupported", UnsupportedProviderMessage(_registry));
        var descriptor = _registry.GetRequired(type).Descriptor;
        if (request.DefaultOsId is <= 0 || request.ProjectId is { Length: > 128 } || request.ProjectId?.Any(char.IsControl) == true)
            throw new ProviderCatalogException("provider_settings_invalid", "The provider account settings are invalid.");
        var now = clock.GetUtcNow();
        var account = new ProviderAccount
        {
            Id = ids.NewId("provider"), OrganizationId = request.OrganizationId, Name = request.Name.Trim(), ProviderType = type,
            ProjectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId.Trim(),
            DefaultOsId = request.DefaultOsId ?? descriptor.Placement.DefaultOsId,
            CreatedAt = now, UpdatedAt = now
        };
        var reference = await WriteCandidateAsync(account, descriptor, request.Credential, ct);
        var candidate = clients.CreateCandidate(account, request.Credential);
        await ProbeAsync(candidate, ct);
        var candidateScope = await VerifyScopeIdentityAsync(account, candidate, descriptor, ct);
        await RejectKnownScopeAliasAsync(account, candidate, candidateScope, ct);
        AssignCredential(account, reference);
        account.ValidationStatus = "valid"; account.LastValidatedAt = now;
        db.ProviderAccounts.Add(account);
        return account;
    }

    public void Update(ProviderAccount account, UpdateProviderRequest request)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        if (request.Name is not null) { ValidateName(request.Name); account.Name = request.Name.Trim(); }
        account.Enabled = request.Enabled ?? account.Enabled;
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
    }

    public async Task ReplaceCredentialsAsync(ProviderAccount account, ReplaceProviderCredentialRequest request, CancellationToken ct)
    {
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var descriptor = _registry.GetRequired(account.ProviderType).Descriptor;
        var reference = await WriteCandidateAsync(account, descriptor, request.Credential, ct);
        var generation = account.CredentialGeneration;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        if (request.Revision != account.Revision) throw ProviderCatalogService.Stale();
        if (account.ValidationStatus == "deleting") throw new ProviderCatalogException("provider_deleting", "Credential cleanup is pending.");
        var candidate = clients.CreateCandidate(account, request.Credential);
        await ProbeAsync(candidate, ct);
        var candidateScope = await VerifyScopeIdentityAsync(account, candidate, descriptor, ct);
        var scopeIdentityMatched = await VerifyCurrentScopeMatchAsync(account, candidateScope, ct);
        await VerifyExistingScopeAsync(account, candidate, ct, scopeIdentityMatched);
        await RejectKnownScopeAliasAsync(account, candidate, candidateScope, ct);
        AssignCredential(account, reference);
        account.CredentialGeneration = generation;
        account.ValidationStatus = "valid"; account.LastValidatedAt = clock.GetUtcNow();
        account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ProviderValidationResponse> ValidateAsync(ProviderAccount account, CancellationToken ct)
    {
        if (account.ValidationStatus == "deleting")
            throw new ProviderCatalogException("provider_deleting", "Provider credential cleanup is pending. Retry deletion.");
        try
        {
            var descriptor = _registry.GetRequired(account.ProviderType).Descriptor;
            var client = await clients.CreateAsync(account.OrganizationId, account.Id, ct);
            await ProbeAsync(client, ct);
            var scopeIdentity = await VerifyScopeIdentityAsync(account, client, descriptor, ct);
            await VerifyExistingScopeAsync(account, client, ct, scopeIdentityVerified: scopeIdentity is not null);
            account.ValidationStatus = "valid";
        }
        catch (ProviderCatalogException ex) when (ex.Code == "provider_scope_unverified") { account.ValidationStatus = "scope_unverified"; }
        catch (HttpRequestException) { account.ValidationStatus = "invalid_or_unavailable"; }
        catch (SecretStoreUnavailableException) { account.ValidationStatus = "credentials_unavailable"; }
        account.LastValidatedAt = clock.GetUtcNow(); account.Revision++; account.UpdatedAt = clock.GetUtcNow();
        return new ProviderValidationResponse(account.ValidationStatus, account.LastValidatedAt.Value);
    }

    public async Task DeleteAsync(ProviderAccount account, CancellationToken ct)
    {
        await EnsureUnusedAsync(account, ct);
        account.Enabled = false; account.ValidationStatus = "deleting"; account.Revision++;
        await db.SaveChangesAsync(ct);
        // The disabled deletion intent is committed before touching OpenBao, so a crash
        // cannot leave an enabled account referring to removed credentials.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProviderCatalogService.LockAccountAsync(db, account.OrganizationId, account.Id, ct);
        await EnsureUnusedAsync(account, ct);
        var cleanup = await db.ProviderCredentialCleanups.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        foreach (var item in cleanup) await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
        if (!cleanup.Any(x => x.Path == account.CredentialPath))
            await secrets.DeleteAsync(new StoredSecretReference(account.CredentialStoreProvider, account.CredentialPath, account.CredentialVersion), ct);
        db.ProviderCredentialCleanups.RemoveRange(cleanup);
        db.ProviderAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // Filter dates in memory as SQLite is used by development installations/tests.
        var pending = (await db.ProviderCredentialCleanups.AsNoTracking().ToListAsync(ct)).Where(x => x.NotBefore <= now).Take(50);
        foreach (var item in pending)
        {
            if (await db.ProviderAccounts.AnyAsync(x => x.CredentialPath == item.Path, ct)) continue;
            await secrets.DeleteAsync(new StoredSecretReference(item.StoreProvider, item.Path, item.Version), ct);
            db.ProviderCredentialCleanups.Remove(item);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<StoredSecretReference> WriteCandidateAsync(ProviderAccount account, ProviderDescriptor descriptor, string credential, CancellationToken ct)
    {
        var validation = descriptor.Credentials.ValidateCredential(account, credential);
        if (!validation.IsValid)
            throw new ProviderCatalogException(validation.ErrorCode ?? "provider_credential_invalid", validation.ErrorMessage ?? "Enter a provider API credential without control characters.");
        var generation = Guid.NewGuid().ToString("N");
        var path = OpenBaoSecretStore.ProviderPathFor(options.Value.EnvironmentPrefix, account.OrganizationId, account.Id, generation);
        db.ProviderCredentialCleanups.Add(new ProviderCredentialCleanup
        {
            Id = ids.NewId("credential_cleanup"), OrganizationId = account.OrganizationId, ProviderAccountId = account.Id,
            Path = path, NotBefore = clock.GetUtcNow().AddDays(1)
        });
        // Write cleanup intent first. It is safe to delete an absent generation.
        await db.SaveChangesAsync(ct);
        var reference = await secrets.PutAsync(new SecretStoreWriteRequest(account.OrganizationId, "", "", "credential", credential)
            { ProviderAccountId = account.Id, CredentialGeneration = generation }, ct);
        account.CredentialGeneration = generation;
        return reference;
    }

    private static void AssignCredential(ProviderAccount account, StoredSecretReference reference)
    {
        account.CredentialStoreProvider = reference.Provider; account.CredentialPath = reference.Path; account.CredentialVersion = reference.Version;
    }

    private static Task ProbeAsync(IVpsProviderClient client, CancellationToken ct) =>
        client.FindServerAsync("selfrun-credential-check-" + Guid.NewGuid().ToString("N"), ct);

    private static async Task<ProviderScopeIdentity?> VerifyScopeIdentityAsync(
        ProviderAccount account,
        IVpsProviderClient client,
        ProviderDescriptor descriptor,
        CancellationToken ct)
    {
        if (client is not IVpsProviderScopeClient scopeClient)
        {
            return null;
        }

        var identity = await scopeClient.VerifyScopeAsync(ct);
        if (string.IsNullOrWhiteSpace(identity.ScopeId))
        {
            throw ScopeUnverified();
        }

        if (!string.IsNullOrWhiteSpace(account.ProjectId) &&
            descriptor.Credentials.ScopeBoundary is ProviderScopeBoundaryKind.Project or ProviderScopeBoundaryKind.ServiceAndProject &&
            !string.Equals(identity.ScopeId, account.ProjectId, StringComparison.Ordinal))
        {
            throw ScopeUnverified();
        }

        return identity;
    }

    private async Task<bool> VerifyCurrentScopeMatchAsync(
        ProviderAccount account,
        ProviderScopeIdentity? candidateScope,
        CancellationToken ct)
    {
        if (candidateScope is null)
        {
            return false;
        }

        try
        {
            var currentClient = await clients.CreateAsync(account.OrganizationId, account.Id, ct);
            if (currentClient is not IVpsProviderScopeClient currentScopeClient)
            {
                return false;
            }

            var currentScope = await currentScopeClient.VerifyScopeAsync(ct);
            if (!currentScope.MatchesScope(candidateScope))
            {
                throw ScopeUnverified();
            }

            return true;
        }
        catch (ProviderCatalogException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SecretStoreUnavailableException)
        {
            // Existing credential may already be revoked; fall back to registered resource verification.
            return false;
        }
    }

    private async Task VerifyExistingScopeAsync(ProviderAccount account, IVpsProviderClient candidate, CancellationToken ct, bool scopeIdentityVerified = false)
    {
        var servers = await db.ServerInstances.Include(x => x.App).ThenInclude(x => x.Organization)
            .Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        var runtimes = await db.RuntimeServers.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        if (runtimes.Any(r => !servers.Any(s => s.ProviderServerId == r.ProviderServerId))) throw ScopeUnverified();
        foreach (var server in servers)
        {
            var name = ServerName(server.App.Organization.Slug, server.App.Slug);
            var observed = await candidate.FindServerAsync(name, ct);
            if (observed is null || observed.Id != server.ProviderServerId || observed.Name != name) throw ScopeUnverified();
        }
        var volumes = await db.AppStorageVolumes.Where(x => x.ProviderAccountId == account.Id).ToListAsync(ct);
        if (volumes.Count > 0 && candidate is not IStorageVolumeProvider) throw ScopeUnverified();
        foreach (var stored in volumes)
        {
            if (stored.ProviderVolumeId is null) throw ScopeUnverified();
            var observed = await ((IStorageVolumeProvider)candidate).GetVolumeAsync(stored.ProviderVolumeId, ct);
            if (observed is null || observed.Name != AppStorageService.VolumeName(stored.AppId, stored.Name)
                || observed.Region != stored.Region || !observed.Labels.TryGetValue("app-id", out var appId) || appId != stored.AppId) throw ScopeUnverified();
        }
        if (await db.Deployments.AnyAsync(x => x.ProviderAccountId == account.Id && x.ProviderCreationAttempted
            && !db.ServerInstances.Any(s => s.ProviderAccountId == account.Id && s.AppId == x.AppId), ct)) throw ScopeUnverified();
        if (!scopeIdentityVerified && servers.Count == 0 && volumes.Count == 0
            && (await db.Deployments.AnyAsync(x => x.ProviderAccountId == account.Id, ct)
                || await db.ProviderPlans.AnyAsync(x => x.ProviderAccountId == account.Id && x.ProvisioningLockedAt != null, ct)))
            throw ScopeUnverified();
    }

    private async Task RejectKnownScopeAliasAsync(ProviderAccount account, IVpsProviderClient candidate, ProviderScopeIdentity? candidateScope, CancellationToken ct)
    {
        if (candidateScope is not null)
        {
            var otherAccounts = await db.ProviderAccounts.AsNoTracking()
                .Where(x => x.Id != account.Id && x.ProviderType == account.ProviderType && x.ValidationStatus != "deleting")
                .ToListAsync(ct);
            foreach (var other in otherAccounts)
            {
                try
                {
                    var otherClient = await clients.CreateAsync(other.OrganizationId, other.Id, ct);
                    if (otherClient is IVpsProviderScopeClient otherScopeClient)
                    {
                        var otherIdentity = await otherScopeClient.VerifyScopeAsync(ct);
                        if (otherIdentity.MatchesScope(candidateScope))
                        {
                            throw new ProviderCatalogException("provider_scope_already_registered",
                                "This cloud scope is already registered through another provider entry. Use that entry instead.");
                        }
                    }
                }
                catch (ProviderCatalogException ex) when (ex.Code == "provider_scope_already_registered")
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or SecretStoreUnavailableException or ProviderCatalogException)
                {
                    // Skip unreachable peer entries during alias detection; server-level check still runs below.
                }
            }
        }

        var otherServers = await db.ServerInstances.Include(x => x.App).ThenInclude(x => x.Organization)
            .Where(x => x.ProviderAccountId != null && x.ProviderAccountId != account.Id && x.Provider == account.ProviderType.ToString()).ToListAsync(ct);
        foreach (var server in otherServers)
        {
            var name = ServerName(server.App.Organization.Slug, server.App.Slug);
            var observed = await candidate.FindServerAsync(name, ct);
            if (observed?.Name == name && observed.Id == server.ProviderServerId)
                throw new ProviderCatalogException("provider_scope_already_registered", "This cloud scope already contains resources registered through another provider entry. Use that entry instead.");
        }
    }

    private async Task EnsureUnusedAsync(ProviderAccount account, CancellationToken ct)
    {
        var plans = await db.ProviderPlans.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var servers = await db.ServerInstances.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var runtimes = await db.RuntimeServers.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var volumes = await db.AppStorageVolumes.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        var reservations = await db.Deployments.CountAsync(x => x.ProviderAccountId == account.Id, ct);
        if (plans + servers + runtimes + volumes + reservations > 0)
            throw new ProviderCatalogException("provider_in_use", $"Provider has dependencies: {plans} plans, {servers} servers, {runtimes} runtimes, {volumes} retained volumes, {reservations} reservations. Disable it instead.");
    }

    public static bool TryParseType(string value, out CloudProvider type) =>
        DefaultRegistry.TryParseBuiltIn(value, out type);

    public static string WireType(CloudProvider type) =>
        DefaultRegistry.GetWireType(type);

    private static string UnsupportedProviderMessage(IVpsProviderRegistry registry)
    {
        var names = registry.GetSupported().Select(x => x.DisplayName).ToArray();
        return names.Length > 0
            ? $"Select a supported provider: {string.Join(", ", names)}."
            : "This VPS provider type is not supported.";
    }

    private static string ServerName(string orgSlug, string appSlug) => ManagedServerName.ForApp(orgSlug, appSlug);
    private static ProviderCatalogException ScopeUnverified() => new("provider_scope_unverified", "Credentials could not be verified against all existing resources. Restore access to the original cloud scope before replacing credentials.");
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ProviderCatalogException("provider_name_invalid", "Use a provider name of 1 to 128 characters without control characters.");
    }
}
