# Provider Expansion Architecture & Adapter Reference

This guide documents the six SDK-independent cloud provider adapters built on top of the shared provider extension architecture (`#352`, see [provider extension architecture specification](specs/provider-extension-architecture.md)):

- **Amazon Web Services (AWS EC2)** — `#346` (`aws`)
- **Microsoft Azure (Virtual Machines / ARM)** — `#347` (`azure`)
- **Google Cloud Platform (Compute Engine)** — `#348` (`gcp`)
- **OVHcloud (Public Cloud + Dedicated Bare Metal contract)** — `#349` (`ovhcloud`)
- **Akamai Connected Cloud / Linode** — `#350` (`linode`)
- **Scaleway (Instances + Block Storage)** — `#351` (`scaleway`)

> **Qualification gate**: None of the six expansion providers are advertised as live-qualified by default. `/api/provider-types` advertises only live-qualified providers (`hetzner`, `digital-ocean`, `vultr`) unless an operator explicitly enables an experimental provider via configuration (`<Provider>:Enabled=true` or `Providers:Registry:EnabledGatedProviders`). Do **not** promote a provider to `Qualified` until disposable live-account qualification passes for that provider.

---

## 1. Shared Provider Extension Architecture (`#352`)

All nine built-in providers register through the shared extension contracts under `modules/common/Providers/Extensibility/`:

| Abstraction | Location | Purpose |
|---|---|---|
| `CloudProvider` | `Providers/CloudProvider.cs` | Canonical provider enum (`Hetzner`, `DigitalOcean`, `Vultr`, `Aws`, `Azure`, `GoogleCloud`, `Ovhcloud`, `Linode`, `Scaleway`). |
| `ProviderDescriptor`, `ProviderCapabilities`, & `BuiltInProviderDescriptors` | `Providers/Extensibility/ProviderCapabilities.cs`, `Providers/Extensibility/ProviderRegistry.cs` | Declares per-provider wire types, display names, capability metadata (`ComputeFamilies`, `SupportedArchitectures`, `LocationGranularity`, `SupportsDurableVolumes`, `SupportsSshKeyInventory`, `SupportsScopeIdentityDiscovery`, `SupportsCatalogDiscovery`, `SupportsQuotaDiscovery`, etc.), credential format validators, non-secret settings, default images, and `ProviderQualificationState` (`Qualified` vs `Experimental`). |
| `IVpsProviderRegistry` & `IVpsProviderRegistration` | `Providers/Extensibility/ProviderRegistry.cs`, `Providers/Extensibility/ExpansionProviderRegistrations.cs` | Registry and DI registration seam (`AddVpsProvider<TRegistration>()`) used by `ProviderAccountClientFactory`, `ProvidersController`, and `ProviderAccountService` so new providers plug in without hardcoding switch chains across API/worker services. |
| `IVpsProviderScopeClient` & `IVpsProviderCatalogDiscoveryClient` | `Providers/Extensibility/ProviderCredentialContracts.cs`, `Providers/Extensibility/ProviderCapabilities.cs` | Standard read-only scope verification (`VerifyScopeAsync`) and normalized location, compute-offer, image, and quota discovery (`ListLocationsAsync`, `ListComputeOffersAsync`, `ListImagesAsync`, `GetQuotasAsync`). |
| `IBareMetalProviderClient` | `Providers/IBareMetalProviderClient.cs` | Dedicated bare-metal contract separating Public Cloud VMs from physical server inventory/provisioning, with an explicit commercial-ordering opt-in gate. |
| `TokenRedactor` | `Providers/Common/TokenRedactor.cs` | Strips bearer tokens, AWS SigV4 credentials/signatures, OVH `$1$` signatures/keys, PEM private keys, `X-Auth-Token`, `clientSecret`, `secretAccessKey`, and `root_pass` from errors and logs. |

### Enabling an Experimental Provider in Non-Production / Qualification Environments

Enable individual adapters via standard ASP.NET Core / worker configuration:

```bash
# Example: enable AWS and Scaleway for isolated qualification testing
Aws__Enabled=true
Scaleway__Enabled=true
```

Or via the central registry feature-gate list (`ProviderRegistryOptions`):

```json
{
  "Providers": {
    "Registry": {
      "EnabledGatedProviders": ["aws", "azure", "gcp", "ovhcloud", "linode", "scaleway"]
    }
  }
}
```

### Write-Only Credential Handling

- All provider credentials remain **write-only** and are persisted exclusively in OpenBao under per-organization, per-account, per-generation paths.
- Structured multi-field credentials are stored as compact single-line JSON (no control characters) and validated via `ProviderDescriptor.Credentials.ValidateCredential` **before** secret storage or cloud HTTP dispatch.
- The CLI (`minicloud providers add`) reads provider credentials strictly from operator environment variables and never accepts secret values as command-line arguments.

---

## 2. Per-Provider Setup, Scopes, and Limitations

### 2.1 Amazon Web Services (`aws`, `#346`)

- **Compute & storage**: EC2 instances (`IVpsProviderClient`), EC2 key pairs (`IVpsProviderSshKeyClient`), EBS gp3 volumes (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and region/SKU/AMI/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
- **Auth & signing**: SDK-independent pure-C# AWS Signature Version 4 (`AWS4-HMAC-SHA256`) per request, supporting short-lived STS `sessionToken` credentials as well as IAM access keys.
- **Credential format (OpenBao JSON)**:
  ```json
  {"accessKeyId":"AKIA...","secretAccessKey":"...","sessionToken":"...","region":"us-east-1","accountId":"123456789012"}
  ```
- **CLI environment variables**: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, optional `AWS_SESSION_TOKEN`, `AWS_DEFAULT_REGION`/`AWS_REGION`, `AWS_ACCOUNT_ID`.
- **Minimum IAM permissions**: Scoped to Minicloud-tagged resources (`minicloud-managed=true`) where possible: `ec2:DescribeRegions`, `ec2:DescribeInstanceTypes`, `ec2:DescribeImages`, `ec2:DescribeInstances`, `ec2:RunInstances`, `ec2:TerminateInstances`, `ec2:CreateTags`, `ec2:DescribeKeyPairs`, `ec2:ImportKeyPair`, `ec2:DeleteKeyPair`, `ec2:CreateVolume`, `ec2:DescribeVolumes`, `ec2:AttachVolume`, `ec2:DetachVolume`, `ec2:DeleteVolume`, `servicequotas:ListServiceQuotas` (optional for quota snapshots).
- **Regions, SKUs, Images & Architectures**: Standard EC2 regions (e.g., `us-east-1`, `eu-central-1`), x86_64 (`t3.small`, `m6i.large`) and ARM64 Graviton (`t4g.small`, `m7g.large`) SKUs, Ubuntu 24.04 LTS AMI IDs per region/architecture.
- **Boundary & cost notes**: Cross-account responses are rejected when `accountId` is configured. Deleting an instance does not automatically release standalone Elastic IPs or detached EBS volumes unless explicitly cleaned up.

### 2.2 Microsoft Azure (`azure`, `#347`)

- **Compute & storage**: Azure Virtual Machines via ARM REST (`IVpsProviderClient`), `Microsoft.Compute/sshPublicKeys` (`IVpsProviderSshKeyClient`), Managed Disks (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and location/SKU/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
- **Auth**: OAuth2 client-credentials flow against Microsoft Entra ID with in-memory token caching, or pre-minted Managed Identity / Workload Identity `accessToken`.
- **Scope (`ProjectId`)**: `<subscriptionId>/<resourceGroup>` (required).
- **Credential format (OpenBao JSON)**:
  ```json
  {"tenantId":"...","clientId":"...","clientSecret":"...","subscriptionId":"...","resourceGroup":"rg-minicloud"}
  ```
  *(Or `{"subscriptionId":"...","resourceGroup":"rg-minicloud","accessToken":"..."}` for Managed Identity / federated token injection.)*
- **CLI environment variables**: `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, plus either (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`) or `AZURE_ACCESS_TOKEN`.
- **Minimum RBAC permissions**: Scoped to the dedicated resource group (`rg-minicloud`) rather than the entire subscription: `Microsoft.Compute/virtualMachines/*`, `Microsoft.Compute/disks/*`, `Microsoft.Compute/sshPublicKeys/*`, `Microsoft.Network/networkInterfaces/*`, `Microsoft.Network/publicIPAddresses/*`, `Microsoft.Network/virtualNetworks/subnets/join/action`, `Microsoft.Compute/locations/usages/read`, `Microsoft.Compute/skus/read`.
- **Partial-allocation cleanup**: Creating an Azure VM allocates a Public IP and NIC before the VM. If VM creation fails (for example, `QuotaExceeded`), the adapter performs best-effort cleanup of the newly created NIC and Public IP in reverse order to prevent orphaned billable network resources.
- **Boundary & limitations**: Subscription and resource-group prefixes are verified on every discovered resource; cross-RG or cross-subscription responses are rejected.

### 2.3 Google Cloud Platform (`gcp`, `#348`)

- **Compute & storage**: Compute Engine instances (`IVpsProviderClient`), project/instance SSH keys (`IVpsProviderSshKeyClient`), Persistent Disks (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and zone/machine-type/image/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
- **Auth**: SDK-independent RS256 OAuth2 JWT bearer assertion signed directly from a Service Account JSON key (with token caching), or pre-minted Workload Identity Federation `accessToken`.
- **Scope (`ProjectId`)**: GCP `project_id` (required).
- **Credential format (OpenBao JSON)**: Standard Google Cloud Service Account JSON (`client_email`, `private_key`, `project_id`, `token_uri`) compacted to a single line, or `{"projectId":"...","accessToken":"..."}`.
- **CLI environment variables**: `GCP_SERVICE_ACCOUNT_JSON` (or `GOOGLE_APPLICATION_CREDENTIALS_JSON`) or `GCP_ACCESS_TOKEN` with `GCP_PROJECT_ID`/`GOOGLE_CLOUD_PROJECT`.
- **Minimum IAM permissions**: Custom role scoped to the project with `compute.instances.get`, `compute.instances.list`, `compute.instances.create`, `compute.instances.delete`, `compute.instances.attachDisk`, `compute.instances.detachDisk`, `compute.instances.setMetadata`, `compute.disks.*`, `compute.zones.list`, `compute.machineTypes.list`, `compute.images.list`, `compute.zoneOperations.get`, `compute.projects.get`.
- **Discovery & safety**: Uses paginated `aggregated/instances` queries across zones, fails safely on duplicate names across zones, rejects cross-project `selfLink` responses, requires an explicit zone operation payload on create/delete, and rejects cross-zone disk attachment.

### 2.4 OVHcloud (`ovhcloud`, `#349`)

- **Compute, storage & bare metal**:
  - **Public Cloud** (`/cloud/project/{projectId}/...`): Instances (`IVpsProviderClient`), SSH keys (`IVpsProviderSshKeyClient`), Cinder block volumes (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and region/flavor/image/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
  - **Dedicated Bare Metal** (`/dedicated/server/...`, `IBareMetalProviderClient`): Read-only server inventory and offer catalog listing, plus explicit reboot/reinstall/provision/cancel contracts.
- **Auth & signing**: SDK-independent OVHcloud APIv6 SHA-1 signature (`$1$<sha1>`) per request (`X-Ovh-Application`, `X-Ovh-Consumer`, `X-Ovh-Timestamp`, `X-Ovh-Signature`). Supports EU, CA, and US API endpoints.
- **Scope (`ProjectId`)**: OVHcloud Public Cloud project ID (required for Public Cloud; optional if only inspecting dedicated inventory).
- **Credential format (OpenBao JSON)**:
  ```json
  {"applicationKey":"...","applicationSecret":"...","consumerKey":"...","projectId":"...","endpoint":"ovh-eu"}
  ```
- **CLI environment variables**: `OVH_APPLICATION_KEY`, `OVH_APPLICATION_SECRET`, `OVH_CONSUMER_KEY`, optional `OVH_PROJECT_ID`, `OVH_ENDPOINT` (`ovh-eu`, `ovh-ca`, `ovh-us`).
- **Bare-metal commercial ordering gate**: `OvhcloudOptions.AllowBareMetalCommercialOrdering` defaults to **`false`**. Attempting to order or cancel a billable dedicated server contract throws a `ProviderApiException` unless an operator explicitly opts in. Read-only dedicated server inventory/offer listing and Public Cloud VM operations do not trigger commercial bare-metal orders.
- **Cost & lifecycle caveats**: Dedicated bare-metal servers have monthly/commitment billing cycles and multi-minute OS reinstall times that differ significantly from hourly Public Cloud VMs; Minicloud exposes `ComputeFamily.BareMetal` in `ProviderDescriptor.Capabilities.ComputeFamilies` so callers treat bare-metal separately from VPS auto-provisioning.

### 2.5 Akamai Connected Cloud / Linode (`linode`, `#350`)

- **Compute & storage**: Linode Instances v4 (`IVpsProviderClient`), account SSH keys (`IVpsProviderSshKeyClient`), Block Storage Volumes (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and region/type/image/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
- **Auth**: Personal Access Token (`Authorization: Bearer <token>`).
- **Credential format (OpenBao)**: Raw Linode Personal Access Token string (or JSON `{"token":"..."}`).
- **CLI environment variables**: `LINODE_TOKEN` or `LINODE_API_TOKEN`.
- **Minimum token scopes**: `linodes:read_write`, `volumes:read_write`, `account:read_only` (SSH key management requires `account:read_write`).
- **Provisioning details**: Uses `X-Filter` headers for exact label discovery, passes cloud-init via base64 `metadata.user_data`, and generates an ephemeral cryptographic `root_pass` (never logged or persisted, and redacted if echoed) to satisfy Linode's instance creation requirement while relying on SSH keys for access.

### 2.6 Scaleway (`scaleway`, `#351`)

- **Compute & storage**: Scaleway Instances API (`IVpsProviderClient`), IAM SSH keys (`IVpsProviderSshKeyClient`), Block Storage volumes (`IStorageVolumeProvider`), scope verification (`IVpsProviderScopeClient`), and zone/commercial-type/image/quota discovery (`IVpsProviderCatalogDiscoveryClient`).
- **Auth**: Scaleway IAM Secret Key (`X-Auth-Token` header).
- **Scope (`ProjectId`)**: Scaleway Project UUID (required; strictly enforced on every request and response).
- **Credential format (OpenBao JSON)**:
  ```json
  {"secretKey":"...","projectId":"...","defaultZone":"fr-par-1"}
  ```
- **CLI environment variables**: `SCW_SECRET_KEY`, `SCW_DEFAULT_PROJECT_ID` (or `SCW_PROJECT_ID`), optional `SCW_DEFAULT_ZONE`.
- **Multi-step provisioning & partial-failure reconciliation**: Scaleway instance launch requires three steps (`POST /servers` -> `PATCH /servers/{id}/user_data/cloud-init` -> `POST /servers/{id}/action` with `poweron`). If cloud-init staging or `poweron` fails after server creation, the adapter throws a `ProviderApiException` containing the created `zone/serverId` so Minicloud's reservation/reconciliation loop can inspect or clean up the stopped instance instead of leaking it.
- **Architectures**: Supports both `x86_64` (`DEV1-S`, `PRO2-*`) and `arm64` (`COPARM1-*`, `PLAY2-*`) commercial types across zones (`fr-par-1`, `nl-ams-1`, `pl-waw-1`).

---

## 3. Live Qualification Checklist (Merge & Promotion Prerequisites)

Before promoting any expansion provider from `Experimental` to `Qualified` (default-enabled in `/api/provider-types`):

1. Run the mock contract suite (`Minicloud.Worker.Tests.Providers.*`, `Minicloud.Tests.ProviderExpansionCatalogApiTests`, and `Minicloud.Tests.ProviderExtensionApiContractTests`) and confirm zero regressions.
2. Provision a **disposable test account/project/resource-group** with tight spending limits and least-privilege credentials.
3. Verify read-only credential check (`POST /api/providers`) and negative credential check (401/403 with secret redaction).
4. Execute one end-to-end disposable lifecycle test: create VM with cloud-init -> SSH readiness -> attach/detach durable volume -> delete VM and volume -> confirm zero leaked network/IP/disk resources in the cloud console.
5. Verify quota/error recovery and credential rotation scope verification against the disposable resource.
6. Update `BuiltInProviderDescriptors` in `modules/common/Providers/Extensibility/ProviderRegistry.cs` to set `QualificationState = ProviderQualificationState.Qualified` only for the verified provider.
