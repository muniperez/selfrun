// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Selfrun.Common.Contracts;

namespace Selfrun.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunProvidersAsync(string[] args, CancellationToken ct)
    {
        if (args.FirstOrDefault() == "import-legacy")
        {
            var path = GetOption(args, "--manifest");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new CliCommandException(CliExitCodes.ValidationError, "Provide an existing JSON manifest with --manifest <path>.");
            ProviderLegacyImportRequest manifest;
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !HasString(root, "organizationId") || !HasString(root, "providerAccountId")
                    || !root.TryGetProperty("appBindings", out var apps) || apps.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("retainedVolumeIds", out var volumes) || volumes.ValueKind != JsonValueKind.Array
                    || apps.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object || !HasString(x, "appId"))
                    || volumes.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString())))
                    throw new JsonException();
                manifest = root.Deserialize<ProviderLegacyImportRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, "Manifest requires organizationId, providerAccountId, appBindings with appId, and retainedVolumeIds arrays. Use an empty array explicitly when there are no matching resources.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliCommandException(CliExitCodes.ValidationError, "The import manifest could not be read.");
            }
            var result = await _apiClient.ImportProviderLegacyAsync(manifest, ct);
            _console.WriteLine($"Imported legacy bindings: plans={result.CreatedPlans}, apps={result.BoundApps}, servers={result.BoundServers}, runtimes={result.BoundRuntimes}, volumes={result.BoundVolumes}.");
            return CliExitCodes.Success;
        }
        if (args.FirstOrDefault() == "ssh-keys")
            return await RunProviderSshKeysAsync(args, ct);
        if (args.FirstOrDefault() != "add")
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: selfrun providers add --organization <id> --name <name> --provider <type> | import-legacy --manifest <path> | ssh-keys <provider-account-id> | --plan <plan> [--organization <id>]");
        var organizationId = GetOption(args, "--organization");
        var name = GetOption(args, "--name");
        var rawProvider = GetOption(args, "--provider")?.ToLowerInvariant();
        var provider = rawProvider switch
        {
            "hetzner" => "hetzner",
            "digital-ocean" or "digitalocean" => "digital-ocean",
            "vultr" => "vultr",
            "aws" or "ec2" => "aws",
            "azure" or "microsoft-azure" => "azure",
            "gcp" or "google-cloud" or "googlecloud" => "gcp",
            "ovhcloud" or "ovh" or "ovh-cloud" => "ovhcloud",
            "linode" or "akamai" or "akamai-linode" => "linode",
            "scaleway" or "scw" => "scaleway",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(name) || provider is null)
            throw new CliCommandException(CliExitCodes.ValidationError, "Specify --organization, --name and --provider hetzner|digital-ocean|vultr|aws|azure|gcp|ovhcloud|linode|scaleway.");
        if (args.Any(x => x.StartsWith("--token", StringComparison.Ordinal) || x.StartsWith("--credential", StringComparison.Ordinal) || x.StartsWith("--api-key", StringComparison.Ordinal)))
            throw new CliCommandException(CliExitCodes.ValidationError, "Cloud credentials must be supplied through provider environment variables, never command arguments.");
        var projectId = GetOption(args, "--project-id") ?? ResolveDefaultProjectId(provider);
        var credential = ResolveProviderCredential(provider, projectId);
        if (credential is null)
            throw new CliCommandException(CliExitCodes.ValidationError, provider switch
            {
                "hetzner" => "Set HCLOUD_TOKEN or HETZNER_API_TOKEN before adding this account.",
                "vultr" => "Set VULTR_API_KEY or VULTR_KEY before adding this account.",
                "digital-ocean" => "Set DIGITALOCEAN_ACCESS_TOKEN or DIGITALOCEAN_TOKEN before adding this account.",
                "aws" => "Set SELFRUN_AWS_CREDENTIAL_JSON or AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY before adding this account.",
                "azure" => "Set SELFRUN_AZURE_CREDENTIAL_JSON or AZURE_SUBSCRIPTION_ID and AZURE_RESOURCE_GROUP with AZURE_ACCESS_TOKEN or AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_CLIENT_SECRET before adding this account.",
                "gcp" => "Set SELFRUN_GCP_CREDENTIAL_JSON, GCP_SERVICE_ACCOUNT_JSON, GOOGLE_APPLICATION_CREDENTIALS_JSON, or GCP_ACCESS_TOKEN before adding this account.",
                "ovhcloud" => "Set SELFRUN_OVH_CREDENTIAL_JSON or OVH_APPLICATION_KEY, OVH_APPLICATION_SECRET, and OVH_CONSUMER_KEY before adding this account.",
                "linode" => "Set SELFRUN_LINODE_TOKEN, LINODE_TOKEN, or LINODE_API_TOKEN before adding this account.",
                "scaleway" => "Set SELFRUN_SCALEWAY_CREDENTIAL_JSON, SCW_SECRET_KEY, or SCALEWAY_SECRET_KEY before adding this account.",
                _ => "Set the provider credential environment variable before adding this account."
            });
        var osValue = GetOption(args, "--os-id");
        if (args.Any(x => x == "--os-id" || x.StartsWith("--os-id=", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(osValue))
            throw new CliCommandException(CliExitCodes.ValidationError, "--os-id requires a positive integer.");
        int? osId = null;
        if (osValue is not null)
        {
            if (!int.TryParse(osValue, out var parsed) || parsed <= 0)
                throw new CliCommandException(CliExitCodes.ValidationError, "--os-id requires a positive integer.");
            osId = parsed;
        }
        var account = await _apiClient.CreateProviderAsync(new(organizationId, name, provider, credential, projectId, osId), ct);
        _console.WriteLine($"Provider added: {account.Name} ({account.Id}, {account.ProviderType}).");
        return CliExitCodes.Success;
    }

    private static string? ResolveDefaultProjectId(string provider) => provider switch
    {
        "digital-ocean" => FirstEnvironmentValue("DIGITALOCEAN_PROJECT_ID"),
        "aws" => FirstEnvironmentValue("AWS_ACCOUNT_ID"),
        "azure" => (FirstEnvironmentValue("AZURE_SUBSCRIPTION_ID"), FirstEnvironmentValue("AZURE_RESOURCE_GROUP")) is ({ } sub, { } rg) ? $"{sub}/{rg}" : FirstEnvironmentValue("AZURE_SUBSCRIPTION_ID"),
        "gcp" => FirstEnvironmentValue("GCP_PROJECT_ID", "GOOGLE_CLOUD_PROJECT"),
        "ovhcloud" => FirstEnvironmentValue("OVH_PROJECT_ID", "OS_TENANT_ID"),
        "scaleway" => FirstEnvironmentValue("SCW_DEFAULT_PROJECT_ID", "SCW_PROJECT_ID", "SCALEWAY_PROJECT_ID"),
        _ => null
    };

    private static string? ResolveProviderCredential(string provider, string? projectId) => provider switch
    {
        "hetzner" => FirstEnvironmentValue("HCLOUD_TOKEN", "HETZNER_API_TOKEN"),
        "vultr" => FirstEnvironmentValue("VULTR_API_KEY", "VULTR_KEY"),
        "digital-ocean" => FirstEnvironmentValue("DIGITALOCEAN_ACCESS_TOKEN", "DIGITALOCEAN_TOKEN"),
        "aws" => FirstEnvironmentValue("SELFRUN_AWS_CREDENTIAL_JSON")
            ?? ((FirstEnvironmentValue("AWS_ACCESS_KEY_ID"), FirstEnvironmentValue("AWS_SECRET_ACCESS_KEY")) is ({ } ak, { } sk)
                ? JsonSerializer.Serialize(new
                {
                    accessKeyId = ak,
                    secretAccessKey = sk,
                    sessionToken = FirstEnvironmentValue("AWS_SESSION_TOKEN"),
                    accountId = projectId ?? FirstEnvironmentValue("AWS_ACCOUNT_ID"),
                    region = FirstEnvironmentValue("AWS_REGION", "AWS_DEFAULT_REGION")
                })
                : null),
        "azure" => FirstEnvironmentValue("SELFRUN_AZURE_CREDENTIAL_JSON")
            ?? ((FirstEnvironmentValue("AZURE_SUBSCRIPTION_ID"), FirstEnvironmentValue("AZURE_RESOURCE_GROUP")) is ({ } sub, { } rg)
                ? (FirstEnvironmentValue("AZURE_ACCESS_TOKEN") is { } token
                    ? JsonSerializer.Serialize(new
                    {
                        subscriptionId = sub,
                        resourceGroup = rg,
                        accessToken = token
                    })
                    : (FirstEnvironmentValue("AZURE_TENANT_ID"), FirstEnvironmentValue("AZURE_CLIENT_ID"), FirstEnvironmentValue("AZURE_CLIENT_SECRET")) is ({ } tid, { } cid, { } csec)
                        ? JsonSerializer.Serialize(new
                        {
                            tenantId = tid,
                            clientId = cid,
                            clientSecret = csec,
                            subscriptionId = sub,
                            resourceGroup = rg
                        })
                        : null)
                : null),
        "gcp" => FirstEnvironmentValue("SELFRUN_GCP_CREDENTIAL_JSON", "GCP_SERVICE_ACCOUNT_JSON", "GOOGLE_APPLICATION_CREDENTIALS_JSON")
            ?? (FirstEnvironmentValue("GCP_ACCESS_TOKEN") is { } gcpToken && (projectId ?? FirstEnvironmentValue("GCP_PROJECT_ID", "GOOGLE_CLOUD_PROJECT")) is { } gcpProject
                ? JsonSerializer.Serialize(new
                {
                    projectId = gcpProject,
                    accessToken = gcpToken
                })
                : null),
        "ovhcloud" => FirstEnvironmentValue("SELFRUN_OVH_CREDENTIAL_JSON")
            ?? ((FirstEnvironmentValue("OVH_APPLICATION_KEY"), FirstEnvironmentValue("OVH_APPLICATION_SECRET"), FirstEnvironmentValue("OVH_CONSUMER_KEY")) is ({ } ak, { } asec, { } ck)
                ? JsonSerializer.Serialize(new
                {
                    applicationKey = ak,
                    applicationSecret = asec,
                    consumerKey = ck,
                    projectId = projectId ?? FirstEnvironmentValue("OVH_PROJECT_ID"),
                    endpoint = FirstEnvironmentValue("OVH_ENDPOINT")
                })
                : null),
        "linode" => FirstEnvironmentValue("SELFRUN_LINODE_TOKEN", "LINODE_TOKEN", "LINODE_API_TOKEN"),
        "scaleway" => FirstEnvironmentValue("SELFRUN_SCALEWAY_CREDENTIAL_JSON", "SCW_SECRET_KEY", "SCALEWAY_SECRET_KEY"),
        _ => null
    };

    private async Task<int> RunProviderSshKeysAsync(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: selfrun providers ssh-keys <provider-account-id> | --plan <plan> [--organization <id>]";
        var accountId = args.ElementAtOrDefault(1) is { } positional && !positional.StartsWith('-') ? positional : null;
        var planName = GetOption(args, "--plan");
        if ((accountId is null) == (planName is null) || (planName is not null && string.IsNullOrWhiteSpace(planName)))
            throw new CliCommandException(CliExitCodes.ValidationError, usage);
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null)
        {
            if (me.Organizations.Count != 1)
                throw new CliCommandException(CliExitCodes.ValidationError, "Select an organization with --organization <id>.");
            organizationId = me.Organizations[0].Id;
        }
        if (!me.Organizations.Any(x => x.Id == organizationId))
            throw new CliCommandException(CliExitCodes.AuthError, "The selected organization is not available to this token.");
        if (planName is not null)
        {
            var plan = (await _apiClient.GetPlansAsync(organizationId, ct)).SingleOrDefault(x => string.Equals(x.Name, planName, StringComparison.Ordinal))
                ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Plan '{planName}' was not found. Run 'selfrun plans list' to see current names.");
            accountId = plan.ProviderAccountId;
            _console.WriteLine($"Provider account: {plan.ProviderName} ({accountId})");
        }
        IReadOnlyList<ProviderSshKeyResponse> keys;
        try
        {
            keys = await _apiClient.GetProviderSshKeysAsync(accountId!, organizationId, ct);
        }
        catch (Api.ApiException ex) when (ex.StatusCode == 403)
        {
            throw new CliCommandException(CliExitCodes.AuthError, $"Auth error: {ex.Message} Listing SSH keys requires an API key with providers:read for this organization.");
        }
        catch (Api.ApiException ex) when (MapSshApiError(ex, null) is { } mapped)
        {
            throw mapped;
        }
        if (keys.Count == 0)
            _console.WriteLine("No selectable SSH keys in this provider account. Use --ssh-public-key-file to add one.");
        foreach (var key in keys)
            _console.WriteLine($"{key.Id}\t{key.Name}\t{key.KeyType}\t{key.Fingerprint}\t{key.RegistrationState}");
        return CliExitCodes.Success;
    }

    private static bool HasString(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static string? FirstEnvironmentValue(params string[] names) => names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}
