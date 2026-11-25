# Provider Extension Architecture Specification

## Status and Scope

This specification defines the shared provider extension architecture referenced by `#352` for adding hosting and VPS providers beyond the currently supported Hetzner, DigitalOcean, and Vultr integrations:

- **AWS (EC2)** — `#346`
- **Microsoft Azure (Virtual Machines)** — `#347`
- **Google Cloud Platform (Compute Engine)** — `#348`
- **OVHcloud (Public Cloud VPS/Instances and Bare Metal)** — `#349`
- **Linode / Akamai Cloud** — `#350`
- **Scaleway (Instances)** — `#351`

This architecture builds on [Provider Accounts and Plans](provider-accounts-and-plans.md). It preserves all existing Hetzner, DigitalOcean, and Vultr contracts and enforces a strict qualification gate so no new provider is advertised in `/api/provider-types`, the CLI, the dashboard, or user-facing docs until its adapter passes contract tests and disposable live qualification.

## Non-Negotiable Invariants

1. **Explicit single-account plan binding**: A plan binds to one organization provider account and one immutable placement (`Region`/location, `Sku`, `Image`, `BuildPlatform`, `ComputeFamily`). Quota or capacity errors never silently switch accounts, regions, or providers.
2. **Write-only credentials in OpenBao**: Provider secrets are stored only in OpenBao, never echoed in API responses, audit records, or logs. All HTTP error bodies and exception messages pass through `TokenRedactor`.
3. **Fail-closed allocation and reconciliation**: Non-idempotent mutations (`POST` server/volume/network/key creation) are never automatically retried by HTTP middleware. Only definite pre-allocation rejections clear reservation/creation markers; transport failures, timeouts, `5xx`, `409`, and post-create partial failures remain unresolved and must reconcile in the original account.
4. **Qualification gating**: Provider registrations default to `Experimental` or `DraftGated` until independently qualified. Only `Qualified` providers (or explicitly allowlisted gated providers in local/qualification environments) appear in `IVpsProviderRegistry.GetSupported()`.
5. **Capability-driven core logic**: API controllers, catalog services, network/storage services, and worker steps inspect `ProviderDescriptor` and `ProviderCapabilities` rather than branching on hardcoded provider names.

---

## 1. Provider Registry and Qualification Gate

**Types**: `modules/common/Providers/Extensibility/ProviderRegistry.cs`

- `IVpsProviderRegistration`: pairs a declarative `ProviderDescriptor`, a provider-specific `IProviderErrorClassifier`, and a client factory `CreateClient(ProviderClientCreationContext)`.
- `ProviderDescriptor`: immutable metadata describing `WireType`, `Aliases`, `DisplayName`, optional `BuiltInProvider`, `QualificationState`, `Capabilities`, `Placement`, `Credentials`, and `RetryPolicy`.
- `ProviderQualificationState`:
  - `Experimental`: early prototype; hidden from production APIs.
  - `DraftGated`: implementation complete behind feature gate; hidden unless explicitly enabled in `Providers:Registry:EnabledGatedProviders` for local/disposable qualification.
  - `Qualified`: passed full mocked contract suite and disposable live qualification; advertised by `GET /api/provider-types` and selectable for new accounts/plans.
  - `Deprecated`: hidden from new account selection while retaining client resolution for existing resources.
- `IVpsProviderRegistry` / `VpsProviderRegistry`:
  - Enforces unique wire types, aliases, and `CloudProvider` bindings at startup.
  - `GetAll()` exposes all registered descriptors for diagnostics and contract verification.
  - `GetSupported()` exposes only `Qualified` providers (plus explicitly allowlisted gated providers via `ProviderRegistryOptions.EnabledGatedProviders`).
  - Registered via `AddSelfrunProviders(...)` and extensible via `services.AddVpsProvider<TRegistration>()` or `services.AddVpsProvider(registration)`.
- **API exposure**: `GET /api/provider-types` projects `IVpsProviderRegistry.GetSupported()` into `ProviderTypeResponse`, preserving all existing fields (`type`, `name`, `supportsStorage`, `defaultImage`, `defaultOsId`) and adding additive `capabilities` (`ProviderTypeCapabilitiesDto`) and `qualificationState`.

---

## 2. Capability Discovery

**Types**: `modules/common/Providers/Extensibility/ProviderCapabilities.cs`

Every provider declares its capabilities through `ProviderCapabilities`:

- **Compute families** (`ProviderComputeFamily`): `VirtualServer`, `BareMetal`, or both.
- **Architectures**: explicit supported build platforms (`linux/amd64`, `linux/arm64`).
- **Location granularity** (`ProviderLocationGranularity`): `Region` (e.g., `nbg1`, `nyc3`, `us-east-1`, `eastus`, `us-east`), `Zone` (e.g., `us-central1-a`, `fr-par-1`), or `Datacenter` (e.g., OVHcloud bare metal datacenters such as `rbx`, `gra`, `sbg`, `bhs`).
- **Feature flags**: SSH key inventory, cloud-init bootstrap, durable block volumes, online/offline volume resize, snapshots, provider-native private networks, public IPv4/IPv6, labels/tags, exact label ownership verification, read-only scope identity discovery, quota discovery, catalog discovery, asynchronous long-running operations, and client idempotency tokens.

Optional read-only discovery contract:

- `IVpsProviderCatalogDiscoveryClient`: `ListLocationsAsync`, `ListComputeOffersAsync`, `ListImagesAsync`, and `GetQuotasAsync`. Calls are strictly read-only and never allocate billable infrastructure. Offers include `ProviderComputeFamily` and optional `EstimatedProvisioningLeadTime` (critical for bare metal offers).

### Provider Capability and Gap Matrix

| Provider | Issue | Compute Family | Location Granularity | Image Format | Credential & Scope Boundary | Idempotency / Async Ops | Durable Volumes / Private Networks | Primary Gaps vs. Current Core |
|---|---|---|---|---|---|---|---|---|
| **Hetzner** | Qualified | VirtualServer | Region (`nbg1`, `fsn1`, `ash`) | `SlugOrId` (`ubuntu-24.04`) | Bearer API token (Project) | Async actions; no client token | Volumes + Resize + Private Networks | Baseline reference implementation |
| **DigitalOcean** | Qualified | VirtualServer | Region (`nyc3`, `fra1`) | `SlugOrId` (`ubuntu-24-04-x64`) | Bearer API token + optional `projectId` | Sync create + project assignment | Volumes/VPCs not yet wired | Post-create project assignment is partial-failure ambiguous |
| **Vultr** | Qualified | VirtualServer | Region (`ewr`, `ams`) | `PositiveIntegerId` (default `2284`) | API key + optional `defaultOsId` (Account) | Sync create | Volumes/VPCs not yet wired | Label-based discovery only; numeric OS IDs |
| **AWS EC2** | `#346` | VirtualServer | Region (`us-east-1`) + optional AZ/Subnet metadata | `AmiOrAlias` (`ami-...`, `resolve:ssm:...`) | IAM access keys / STS role (`AccountAndRole`), STS `GetCallerIdentity` | `ClientToken` idempotency; async instance/VPC/SG readiness | EBS volumes + VPC/SG prerequisites | SigV4 signing, VPC/subnet/SG bootstrapping or selection, EBS/EIP lifecycle, regional AMIs |
| **Azure VMs** | `#347` | VirtualServer | Region (`eastus`, `westeurope`) + Resource Group | `UrnOrResourceId` (`Canonical:ubuntu-24_04-lts:...`) | Entra Service Principal / Workload Identity (`TenantAndSubscription`) | ARM long-running `Azure-AsyncOperation` polling | Managed Disks + VNet/NSG/NIC/PublicIP | Multi-resource creation/cleanup graph (RG, VNet, NSG, NIC, Public IP, VM) |
| **GCP Compute Engine** | `#348` | VirtualServer | Zone (`us-central1-a`) | `ImageFamilyOrSelfLink` (`projects/ubuntu-os-cloud/global/images/family/...`) | Service Account JSON / Workload Identity (`ServiceAndProject`) | `requestId` UUID idempotency; zonal/regional Operations API | Persistent Disks + VPC/Firewall rules | Zone-level placement, metadata `user-data` vs `ssh-keys`, firewall rules |
| **OVHcloud Public Cloud** | `#349` | VirtualServer | Region (`GRA11`, `SBG5`, `BHS5`) | `SlugOrId` (OpenStack image name/UUID) | OVH API AppKey/AppSecret/ConsumerKey or OAuth2 + `serviceName` (`ServiceAndProject`) | Async OpenStack instance status polling | Cinder volumes + vRack private networks | Dual API/auth signing (`X-Ovh-Signature`), OpenStack project scoping |
| **OVHcloud Bare Metal** | `#349` | BareMetal | Datacenter (`gra`, `rbx`, `sbg`, `bhs`) | `SlugOrId` (OS template name) | OVH API AppKey/AppSecret/ConsumerKey + `serviceName` (`Account` / Dedicated) | Long-running order/reinstall tasks (minutes to hours) | Local RAID/NVMe + vRack (no standard hot-plug cloud volumes) | Commercial order/commitment gating, long provisioning lead time, OS reinstall & post-install bootstrap instead of cloud-init |
| **Linode / Akamai** | `#350` | VirtualServer | Region (`us-east`, `eu-central`) | `SlugOrId` (`linode/ubuntu24.04`) | Personal Access Token (`Account`), `/v4/account` scope identity | Async boot/status polling | Block Storage Volumes + VLAN/VPC | Metadata service (`metadata.user_data`) availability per region, root_pass/SSH key handling |
| **Scaleway** | `#351` | VirtualServer | Zone (`fr-par-1`, `nl-ams-1`, `pl-waw-1`) | `SlugOrId` (Marketplace/image UUID or label) | Secret Key + `project_id` (`Project`) | Async server action (`poweron`) | Block Volumes + Private Networks | Zone-scoped endpoints, explicit `project_id`, two-step create + `user_data` + power-on |

---

## 3. Placement Contracts

**Types**: `modules/common/Providers/Extensibility/ProviderPlacementContracts.cs`

- `ProviderPlacement` captures the immutable placement snapshot (`ProviderWireType`, `ProviderAccountId`, `Region`, `Sku`, `Image`, `BuildPlatform`, `ComputeFamily`, optional `PlacementMetadata` for provider-specific non-secret fields like AWS subnet/VPC ID, Azure resource group, or OVHcloud OS template).
- `ProviderPlacementContract` defines per-provider location granularity, supported compute families, supported build platforms, image format (`ProviderImageFormat`), defaults (`DefaultImage` / `DefaultOsId`), whether an explicit image is required, and optional `CustomValidator`.
- `ProviderPlacementValidator.Validate(descriptor, placement)` enforces:
  - Compute family compatibility (rejects `BareMetal` on virtual-server-only providers with `plan_compute_family_unsupported`).
  - Region/zone/datacenter and SKU length (`<= 128`) and control-character checks.
  - Architecture compatibility (`linux/amd64` or `linux/arm64`) against both the placement contract and provider capabilities.
  - Image format rules (`PositiveIntegerId`, `AmiOrAlias`, `UrnOrResourceId`, `ImageFamilyOrSelfLink`, `SlugOrId`).
  - Optional provider-specific placement rules via `CustomValidator`.
- `ProviderCatalogService` delegates plan placement validation and default image resolution to `IVpsProviderRegistry` + `ProviderPlacementValidator`, preserving all existing plan locking and error codes (`plan_mapping_invalid`, `plan_mapping_locked`).

---

## 4. Credential, Scope, and Ownership Boundaries

**Types**: `modules/common/Providers/Extensibility/ProviderCredentialContracts.cs`

### Credential & Scope Boundary Contract

- `ProviderCredentialFormat`: `ApiToken` (Hetzner, DO, Vultr, Linode, Scaleway), `KeyPairOrStructuredJson` (AWS IAM keys, Azure Service Principal JSON, GCP Service Account JSON, OVHcloud signed key bundle), or `WorkloadIdentity` (federated STS/OIDC/WIF when hosted on cloud infrastructure).
- `ProviderScopeBoundaryKind`: `Account`, `Project`, `AccountAndRole`, `TenantAndSubscription`, `ServiceAndProject`.
- `ProviderCredentialContract`: declares write-only OpenBao storage (`EnforcesWriteOnlyStorage = true`), environment variable names for CLI import, non-secret settings (`ProviderSettingField` with `ImmutableAfterReservation`), and optional credential format validation before persisting to OpenBao.
- `IVpsProviderScopeClient`: optional read-only `VerifyScopeAsync()` returning `ProviderScopeIdentity(ProviderWireType, ScopeKind, ScopeId, SecondaryScopeId, ObservedPrincipal)`.
  - `ProviderAccountService.ReplaceCredentialsAsync` uses `IVpsProviderScopeClient` (when implemented by the adapter) to verify that replacement credentials resolve to the **exact same** cloud account/tenant/subscription/project scope as the previous credentials, rejecting cross-account/cross-project repurposing even before verifying individual resources.
  - `ProviderAccountService` can also verify read-only scope identity during initial creation when supported.

### Least-Privilege Credential Guidance for Planned Providers

- **AWS (`#346`)**: Dedicated IAM user/role scoped to a single AWS account and required EC2/VPC/EBS/STS permissions restricted by `selfrun` resource tags where supported; never root credentials.
- **Azure (`#347`)**: Dedicated Entra application / managed identity scoped to a dedicated Resource Group (or single Subscription with narrow VM/Network/Disk contributor role); never Tenant-wide Owner.
- **GCP (`#348`)**: Dedicated Service Account in a single GCP project with narrow Compute Instance/Disk/Network admin roles (no Project Owner or Billing Admin).
- **OVHcloud (`#349`)**: Dedicated OAuth2 client or scoped Consumer Key restricted to the target `serviceName` (Public Cloud project ID or dedicated bare-metal service list) with read/write limited to required compute/network endpoints.
- **Linode (`#350`)**: Scoped Personal Access Token limited to Linodes, Images, Account (read-only), SSH Keys, and optional Volumes/VPCs.
- **Scaleway (`#351`)**: Scoped IAM API key bound to a single Scaleway `project_id` with Instances, Block Storage, and VPC permissions only.

### Ownership Evidence and Verification

- `ProviderOwnershipEvidence` standardizes deterministic naming (`ManagedServerName.ForApp`), labels (`managed-by=selfrun`, `customer`, `app`, `env`), and tags (`selfrun`, `selfrun-customer-...`, `selfrun-app-...`, `selfrun-env-...`).
- `ProviderOwnershipVerifier.VerifyDiscoveredServer(descriptor, discovered, expectedServerName, ownership)` validates exact server name and, when `descriptor.Capabilities.SupportsExactLabelOwnershipVerification` is true, exact label ownership. `ResolveProvisionServerStep` uses this verifier rather than provider-specific `if` branches.

---

## 5. Lifecycle and Reconciliation Hooks

**Types**: `modules/common/Providers/Extensibility/ProviderLifecycleHooks.cs`

To support both synchronous VPS APIs and multi-step/asynchronous cloud or bare-metal providers without breaking existing `IVpsProviderClient` adapters:

- `IVpsProviderLifecycleClient`: structured hooks for:
  - `AllocateServerAsync(ProviderAllocationRequest)` returning `ProviderAllocationResult` (`Allocated`, `ProvisioningAsync`, `PartialFailureRequiresReconciliation`) with optional `OperationId` and `PartialFailureDetail`.
  - `ReconcileServerAsync(ProviderReconciliationQuery)` returning `ProviderReconciliationResult` (`NotFound`, `Matched`, `AmbiguousMultipleMatches`, `OwnershipMismatch`, `PartiallyAllocated`).
  - `DeleteServerWithVerificationAsync(serverId, expectedOwnership)` returning `ProviderCleanupResult` (`Deleted`, `AlreadyAbsent`, `DeletingAsync`, `OwnershipConflict`), ensuring multi-resource clouds (AWS NIC/EIP/SG, Azure NIC/NSG/PublicIP/Disk, GCP Disk/Firewall) clean up cleanly without stranding billable resources.
- `IVpsProviderAsyncOperationClient`: `GetOperationStatusAsync(operationId)` returning `ProviderOperationStatus` (`Pending`, `Running`, `Succeeded`, `Failed`, `Canceled`, `NotFound`) with `ProgressPercent`, `ResourceId`, and `IsTerminal` for ARM async ops, GCP zone operations, OVHcloud tasks, and bare-metal installs.
- `VpsProviderLifecycleAdapter`: default bridge that adapts any existing `IVpsProviderClient` (Hetzner, DigitalOcean, Vultr) into `IVpsProviderLifecycleClient` while classifying definite vs. ambiguous errors through `ClassifyError(...)` and `IProviderErrorClassifier`.
- `ProviderBootstrapReadiness`: tracks readiness milestones (`ProviderReportsRunning`, `HasPublicAddress`, `SshPortReachable`, `HostKeyVerified`, `CloudInitCompleted`, `EstimatedProvisioningLeadTime`) and computes `IsReadyForDeployment`.

### Bare-Metal Considerations (OVHcloud `#349`)

Bare-metal servers differ materially from cloud VPS instances:
1. **Provisioning lead time**: allocation/OS install takes minutes to hours rather than seconds; adapters must surface `ProviderAllocationState.ProvisioningAsync` and `EstimatedProvisioningLeadTime` rather than blocking worker threads synchronously.
2. **Commercial commitment gating**: bare-metal ordering can incur non-hourly billing commitments; plans targeting `ProviderComputeFamily.BareMetal` must require explicit opt-in metadata/policy before order submission.
3. **Bootstrap path**: bare-metal OS templates may use post-install scripts or SSH key injection rather than cloud-init; `ProviderCapabilities.SupportsCloudInit` and `ProviderBootstrapReadiness` govern whether the worker expects cloud-init host-key enrollment or an explicit operator host-pin / post-install enrollment hook.

---

## 6. Retry and Error Taxonomy

**Types**: `modules/common/Providers/Extensibility/ProviderErrorTaxonomy.cs`, `modules/common/Providers/Common/ProviderApiException.cs`, `modules/common/Providers/Common/ResilientHttpRetryHandler.cs`

### Error Categories and Dispositions

`IProviderErrorClassifier` classifies every provider exception into `(ProviderErrorCategory, ProviderErrorDisposition, IsRetryable, SafeOperatorMessage, ProviderErrorCode)`:

| Category | HTTP / Signal Examples | `ReadOnly` Disposition | `NonIdempotentMutation` Disposition | Effect on Creation Lock |
|---|---|---|---|---|
| `Authentication` | `401`, `SecretStoreUnavailableException` | `DefiniteRejection` | `DefiniteRejection` | Safe to clear |
| `Authorization` | `403` (non-partial) | `DefiniteRejection` | `DefiniteRejection` | Safe to clear |
| `Validation` | `400`, `422` (non-partial) | `DefiniteRejection` | `DefiniteRejection` | Safe to clear |
| `QuotaExceeded` | `402`, or `400`/`403`/`422`/`429` with quota/limit/capacity error code | `DefiniteRejection` | `DefiniteRejection` | Safe to clear; keep credentials valid; never switch accounts |
| `RateLimited` | `429` (non-quota) | `RetryableRead` | `DefiniteRejection` | Safe to clear on pre-create rejection; retry reads honoring `Retry-After` |
| `NotFound` | `404` | `DefiniteRejection` | `AmbiguousOutcome` | Preserved conservatively unless adapter explicitly marks `Validation` |
| `Conflict` | `409` | `AmbiguousOutcome` | `AmbiguousOutcome` | Preserved; reconcile in original account |
| `Timeout` | `408`, `504`, `TimeoutException`, `OperationCanceledException` | `RetryableRead` | `AmbiguousOutcome` | Preserved; reconcile in original account |
| `TransientProviderError` | `5xx`, `HttpRequestException` | `RetryableRead` | `AmbiguousOutcome` | Preserved; reconcile in original account |
| `PartialAllocationAmbiguous` | Post-create sub-step fails (e.g., DO project assign, Azure NIC attach, Scaleway poweron) | `AmbiguousOutcome` | `AmbiguousOutcome` | **Always preserved** even on `400`/`403`/`429`; reconcile in original account |
| `UnsupportedCapability` | Feature unsupported by provider | `DefiniteRejection` | `DefiniteRejection` | Safe to clear |

### HTTP Retry Policy

- `ResilientHttpRetryHandler` inspects `ProviderHttpOptions.NoRetry` (aliased by `HetznerClient.NoRetry` for compatibility) and `ProviderHttpOptions.OperationSafety`.
- Requests tagged `NoRetry = true` or `OperationSafety = NonIdempotentMutation` are **never** retried automatically by HTTP middleware.
- `GET` / `HEAD` / `ReadOnly` probes retry `429` and `5xx`/network failures with bounded exponential backoff + jitter and honor `Retry-After` headers.
- `ProviderApiException` always redacts secrets from `Message` and `ResponseBody` through `TokenRedactor` and carries optional structured fields (`ProviderWireType`, `Category`, `ProviderErrorCode`, `IsAmbiguousOutcome`).

---

## 7. Delivery Sequence, Contract Tests, and Qualification Checklist

Each provider issue (`#346`–`#351`) MUST follow this incremental delivery gate:

1. **Register behind qualification gate**: Implement `IVpsProviderRegistration` with `ProviderQualificationState.DraftGated` (never `Qualified` in the initial PR unless disposable live qualification has already passed and evidence is attached).
2. **Pass shared mocked contract tests**:
   - Registry registration, alias resolution, qualification gate visibility, and `/api/provider-types` exclusion while gated.
   - Placement validation (valid + invalid regions/zones/SKUs/images/architectures/compute families).
   - Credential validation, write-only OpenBao storage, token redaction in all error paths, and `IVpsProviderScopeClient` cross-account rotation rejection.
   - Lifecycle allocation, deterministic discovery + ownership verification, async operation polling (where applicable), partial-failure recovery (create succeeds + secondary step fails retains creation lock and prevents duplicate creation), and clean deletion/cleanup.
   - Error taxonomy classification (definite rejection vs. ambiguous outcome vs. retryable read) and `ResilientHttpRetryHandler` non-retry guarantees on mutations.
3. **Pass disposable live qualification (separate operator run, never in CI)**:
   - Use a disposable cloud account/project/subscription with least-privilege credentials.
   - Provision and destroy at least one `linux/amd64` server and (where supported by the provider) one `linux/arm64` server; for OVHcloud `#349`, qualify Public Cloud VPS and Bare Metal separately.
   - Verify cloud-init / post-install bootstrap, SSH host-key enrollment, durable volume/private network lifecycle (if declared supported), and zero leaked resources after cleanup.
   - Record redacted verification evidence in the provider's issue before promoting `QualificationState` to `ProviderQualificationState.Qualified` and updating user-facing docs.
