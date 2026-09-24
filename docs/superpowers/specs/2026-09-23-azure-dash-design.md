# azure-dash Design Spec

_Date: 2026-09-23 · Status: approved design, pending implementation plan_


## Context

gcp-dash (github.com/kenmoini/gcp-dash) is a FastAPI + Jinja2 + htmx demo app. It lists GCP resources using Application Default Credentials (ADC) and offers liveness, readiness, CPU-burn and crash controls. It also shows container, Kubernetes and cgroup runtime info. It ships Kustomize overlays for GKE Workload Identity, a static service-account key, and OpenShift ZTWIM, where a SPIFFE JWT-SVID is exchanged for a Google token through Workload Identity Federation.

azure-dash is the Azure/.NET counterpart. It demonstrates Microsoft Entra Workload ID on AKS, and also federation from OpenShift, both through ZTWIM/SPIRE and through the cluster's own service-account issuer. The repo `/Users/kemo/Development/azure-dash` is empty (it's on `main` with no commits yet).

**User decisions:**
- **Auth modes:** all four
  - AKS Workload ID
  - OpenShift ZTWIM (SPIRE)
  - static client secret
  - OpenShift native service-account issuer
- **Panels:** the parity set plus a Resource Graph summary
- **UI:** Razor Pages + htmx
- **Extras:** an Identity panel and an IMDS panel
- **Out of scope:** extra chaos controls (memory, latency, startup delay), Prometheus metrics, AKS/Key Vault panels, data-plane demo

**Process:** this spec is followed by an implementation plan in `docs/superpowers/plans/`, executed task by task using TDD.

## Key research findings / gaps vs GCP (these drive the design)

1. **There is no "project" in Azure.** The closest scope is tenant → subscription → resource group.
   - `AZURE_SUBSCRIPTION_ID` is optional. If it's unset, use the only subscription the identity can see. If it sees several, use the first and show a warning.
   - Optional `AZURE_RESOURCE_GROUP` narrows every panel to that resource group. This supports least-privilege Reader at resource-group scope, where listing subscriptions returns nothing.
2. **Credential choice is explicit.** Microsoft advises against `DefaultAzureCredential` in production. A `CredentialFactory` picks the credential from `AUTH_MODE`:

   | `AUTH_MODE` | Credential | How `auto` detects it |
   |---|---|---|
   | `workload-identity` | `WorkloadIdentityCredential` | `AZURE_FEDERATED_TOKEN_FILE` is set |
   | `spiffe` | `ClientAssertionCredential` + callback | `SPIFFE_JWT_FILE` is set |
   | `client-secret` | `ClientSecretCredential` | `AZURE_CLIENT_SECRET` is set |
   | `dev` | `ChainedTokenCredential(AzureCli, AzureDeveloperCli)` | none of the above |

   Register one singleton credential so its token cache is reused.
3. **The ZTWIM token lasts only 5 minutes by default.** `WorkloadIdentityCredential` may cache the file it read, so the SPIFFE mode uses `ClientAssertionCredential`, whose callback **re-reads the spiffe-helper file on every exchange**. The callback accepts either a raw JWT or a base64-wrapped one, because Red Hat's docs say "base64-encoded".
4. **Entra requirements for SPIRE federation:**
   - The OIDC discovery endpoint and `/keys` must be public over HTTPS with a publicly trusted certificate.
   - Tokens must be **RS256**: set `SpireServer.jwtKeyType: rsa-2048`, because upstream SPIRE defaults to ES256.
   - JWKS keys need `"use":"sig"`.
   - The audience is `api://AzureADTokenExchange`, and the federated credential's subject is the SPIFFE ID.
   - All of this goes in the ZTWIM README as a checklist, with `curl` checks.
5. **OpenShift native issuer.** Use an explicit projected `serviceAccountToken` volume with audience `api://AzureADTokenExchange`, plus env vars set by hand. This works whether or not the pod-identity webhook is present (ARO has it; clusters built with `ccoctl` manual mode may; other OCP clusters won't). It reuses the `workload-identity` code path. The README covers finding the issuer on ARO and the `ccoctl`/public-blob issuer on self-managed OCP.
6. **IMDS** (`169.254.169.254`):
   - It describes the **node's VMSS instance**, not the pod, and its resource group is `MC_…`.
   - It may be blocked by AKS `--enable-imds-restriction`, and it's absent off Azure.
   - The client uses a 1 s timeout and no proxy, caches the result, and treats "unavailable" as a normal, displayable state.
7. **Tokens and the token file path:**
   - Never hard-code the token path; always use `AZURE_FEDERATED_TOKEN_FILE`.
   - Request scope `https://management.azure.com/.default`.
   - Access tokens are v1 or v2, so the app handles both `appid` and `azp`.
8. **Sovereign clouds:** set `AZURE_AUTHORITY_HOST` (injected on AKS) and a matching `ArmEnvironment`. An optional `AZURE_CLOUD` (`public|usgov|china`) sets both.
9. **Role propagation:** a Reader role assignment can take up to 10 minutes to apply, and a federated credential takes seconds to minutes. Both go in the troubleshooting section.
10. **Known gcp-dash bug, avoided here:** in `deploy/ztwim`, `entrypoint.sh` uses `$(VAR)` inside an unquoted heredoc. Bash treats that as command substitution, so the generated key.json fields come out empty. azure-dash avoids this entirely: it needs no credential-config file, only helper.conf.

## Tech stack

- **Runtime:** .NET 10 LTS (`net10.0`), ASP.NET Core Razor Pages + minimal APIs, htmx 2.x vendored in `wwwroot`.
- **Azure SDK packages:**
  - Azure.Identity 1.21.x
  - Azure.ResourceManager 1.14.x, with .Compute, .Network, .Storage and .ResourceGraph
  - Pin exact versions in `Directory.Packages.props` (central package management).
- **Tests:** xUnit + `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory). No mocking library; hand-written fakes, as in gcp-dash's `FakeGcpProvider`.
- **Container:** multi-stage build.
  - Build stage: `mcr.microsoft.com/dotnet/sdk:10.0` on `--platform=$BUILDPLATFORM` with `-a $TARGETARCH`. The build cross-compiles, so QEMU only runs for the final stage.
  - Runtime stage: `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu noble, has a shell for debugging).
  - Runs as `USER $APP_UID` (1654) on port 8080. It works under OpenShift's arbitrary UID because the app writes nothing to disk.
  - `HEALTHCHECK` runs `dotnet AzureDash.dll --healthcheck`, a self-probe mode, because the image has no curl.

## Project layout

```
azure-dash.slnx, Directory.Build.props, Directory.Packages.props, global.json
src/AzureDash/
  Program.cs                      # builds the app; exposes a partial Program for WebApplicationFactory
  Configuration/AppSettings.cs    # env → options (mirrors gcp_dash/config.py)
  State/RuntimeState.cs           # live/ready flags, StartedAt (lock)            ← state.py
  Load/CpuLoad.cs                 # N background spin threads, clamp to ProcessorCount ← cpu_load.py
  Runtime/RuntimeInfoProvider.cs  # container/k8s/cgroup v1+v2, injectable fs root ← runtime_info.py
  Identity/AuthMode.cs, CredentialFactory.cs, SpiffeAssertionSource.cs,
           JwtDisplay.cs (decode only, no verification), IdentityInfoService.cs
  Imds/ImdsClient.cs              # HttpClient (UseProxy=false, 1s timeout), cached
  Azure/Models.cs                 # records: SubscriptionInfo, ResourceGroupInfo, VmInfo, StorageAccountInfo,
                                  #   VnetInfo, SubnetInfo, NsgRuleInfo, ResourceTypeCount
  Azure/IAzureProvider.cs, LiveAzureProvider.cs, AzureError.cs
  Azure/TtlCache.cs               # per-key SemaphoreSlim, keep last good value on error, epoch on invalidate ← cache.py
  Azure/AzureService.cs           # lazy provider, kind → loader, logs timing   ← gcp/service.py
  Endpoints/Health.cs, Controls.cs, Api.cs   # minimal APIs; HX-Request → partial HTML, else JSON
  Pages/Index, Azure, Identity (.cshtml), Shared/_Layout, Partials/_Runtime, _Controls,
        _Panel (error/stale wrapper), _Subscription, _ResourceGroups, _Vms, _StorageAccounts,
        _Vnets, _Subnets, _NsgRules, _ResourceGraph, _Imds, _Identity
  wwwroot/css/site.css (ported from gcp-dash style.css, Azure-blue accent), wwwroot/js/htmx.min.js
tests/AzureDash.Tests/            # FakeAzureProvider, FakeImdsHandler, temp-dir cgroup fixtures
Containerfile, .containerignore, .gitignore, .github/workflows/build-container.yaml, .github/dependabot.yml
scripts/run-podman.sh             # passes AZURE_* env / mounts ~/.azure for dev mode
deploy/kubernetes/ (base) · deploy/aks/ · deploy/openshift/ (client secret) · deploy/openshift-wi/ · deploy/ztwim/
README.md, deploy/README.md (shared Azure prereqs: identity + Reader role assignment)
```

**Partial rendering:** fragments are static-rendered Razor components (`Components/Partials/*.razor`), returned from minimal-API endpoints via `RazorComponentResult<T>`. The full Razor Pages embed the same components (`<component type="..." render-mode="Static" />`), so each fragment has one template. Each endpoint returns the fragment when the `HX-Request: true` header is present and JSON otherwise, matching gcp-dash. (The `Pages/Partials/_*.cshtml` names in the layout above therefore become `Components/Partials/*.razor`.)

## Routes (parity with gcp-dash, plus new ones)

| Route | Notes |
|---|---|
| `GET /` | Runtime page, refreshed every 5 s, plus Controls |
| `GET /azure` | Header card showing subscription/RG, "Refresh all", and 8 lazy-loaded panels |
| `GET /identity` | **new**: Identity and IMDS panels |
| `GET /partials/runtime`, `/partials/controls`, `/partials/azure/{kind}[?refresh=1]`, `/partials/identity`, `/partials/imds` | HTML fragments |
| `GET /healthz/live`, `/healthz/ready` | 200 or 503 JSON, same wording as gcp-dash |
| `POST /controls/liveness|readiness|cpu|crash` | Blocked with 403 when `CONTROLS_ENABLED=false`; crash accepts exit code 0–255 (else 422), returns 202, and exits via an injectable `Action<int>` after 500 ms |
| `GET /api/state`, `/api/runtime`, `/api/azure/{kind}`, `/api/identity`, `/api/imds`; `POST /api/azure/refresh` (204) | JSON |

Azure panel kinds and what each shows:

| Kind | Fields |
|---|---|
| `subscription` | id, name, state, tenant |
| `resourcegroups` | name, location, provisioning state |
| `vms` | VMs **and** VMSS: name, RG, location, zone, size, power state via `statusOnly`, private/public IP where cheap |
| `storageaccounts` | name, RG, location, kind, SKU, access tier, public network access |
| `vnets` | name, RG, location, address space, subnet count |
| `subnets` | vnet, name, prefix, NSG, route table |
| `nsgrules` | nsg, rule, direction, priority, access, protocol, ports, source → destination (flattened) |
| `resourcegraph` | KQL `Resources \| summarize count() by type \| order by count_ desc` across readable subscriptions, first 1000 rows |

**Error handling:** exceptions are wrapped as `AzureError("{Type}: {Message}")` and stored in the cache entry. The page never returns a 500, and the last good data stays visible under a "showing cached data" note. `RequestFailedException` 403 responses get a hint such as "grant Reader on scope X; role assignments can take ~10 min". Credential failures show in every panel, and the provider is retried on the next fetch.

**Identity panel:**
- auth mode, and why `auto` chose it
- credential type, client ID, tenant ID, authority host, token file path
- **decoded federated token**: `iss`, `sub`, `aud`, `iat`, `exp`, time remaining; for SPIFFE, the `sub` is the SPIFFE ID
- **decoded ARM access-token claims**: `aud`, `iss`, `oid`, `tid`, `appid`/`azp`, `idtyp`, `ver`, `xms_mirid` if present, `exp`

Raw tokens are never rendered or logged, and the logging redaction is ported from `logging_setup.py`.

**IMDS panel:** `/metadata/instance/compute`. It shows name, location, zone, vmSize, subscriptionId, resourceGroupName, vmScaleSetName, osType and vmId, with the caption "this is the node, not the pod". Otherwise it shows unavailable or blocked. `api-version` is configurable.

**Runtime info:** ports everything gcp-dash shows (hostname, container runtime detection, Downward API pod fields, cgroup CPU/memory, OS, uid/gid/pid, CPUs, load average, uptime). It swaps the Python version for the .NET version, the GC mode and `Environment.ProcessorCount`.

## Configuration (env)

| Var | Default |
|---|---|
| `PORT` (via `ASPNETCORE_HTTP_PORTS`) | 8080 |
| `AUTH_MODE` | auto |
| `AZURE_SUBSCRIPTION_ID` | — |
| `AZURE_RESOURCE_GROUP` | — |
| `AZURE_CLOUD` | public |
| `AZURE_CACHE_TTL_SECONDS` | 60 |
| `CONTROLS_ENABLED` | true |
| `LOG_LEVEL` | Information |
| `AZURE_DEBUG` | false (turns on Azure SDK event-source logging, redacted) |
| `SPIFFE_JWT_FILE` | — |
| `IMDS_ENABLED` | true |
| `IMDS_API_VERSION` | `2021-02-01` (widely supported; override if needed) |

Also read:
- Downward API: `POD_NAME`, `POD_NAMESPACE`, `NODE_NAME`, `POD_IP`, `SERVICE_ACCOUNT`
- Standard Azure variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE`, `AZURE_AUTHORITY_HOST`, `AZURE_CLIENT_SECRET`

## Deployment overlays (Kustomize, same shape as gcp-dash)

- **`deploy/kubernetes` (base):**
  - Namespace `azure-dash`, ConfigMap, ServiceAccount `azure-dash`, Service on 8080.
  - Deployment: 1 replica, non-root, drops ALL capabilities, requests 100m/128Mi, limits 1 CPU/512Mi, Downward API env, probes as in gcp-dash, empty `initContainers`/`volumes` for overlays to patch.
  - Image: `ghcr.io/kenmoini/azure-dash:latest`.
- **`deploy/aks`:**
  - Service account annotation `azure.workload.identity/client-id`.
  - Pod label `azure.workload.identity/use: "true"`.
  - An Ingress using the AKS app-routing class `webapprouting.kubernetes.azure.com`.
  - README steps:
    1. `az aks update --enable-oidc-issuer --enable-workload-identity`
    2. create a user-assigned managed identity (UAMI)
    3. `az identity federated-credential create` with subject `system:serviceaccount:azure-dash:azure-dash` and audience `api://AzureADTokenExchange`
    4. `az role assignment create --role Reader`
    5. apply, then restart
- **`deploy/openshift` (client secret):**
  - Route, plus `envFrom` Secret `azure-dash-sp` (`AZURE_TENANT_ID`/`CLIENT_ID`/`CLIENT_SECRET`, `optional: true`) and `secret.example.yaml`.
  - README: `az ad sp create-for-rbac --role Reader --scopes …`.
- **`deploy/openshift-wi` (native issuer):**
  - Route, a projected `serviceAccountToken` volume (audience `api://AzureADTokenExchange`, 3600 s), and env `AZURE_FEDERATED_TOKEN_FILE`/`CLIENT_ID`/`TENANT_ID`/`AUTHORITY_HOST`.
  - README: find the issuer (`oc get authentication cluster -o jsonpath='{.spec.serviceAccountIssuer}'`), check that it's publicly discoverable, then create the federated credential with subject `system:serviceaccount:<ns>:azure-dash`.
- **`deploy/ztwim`:**
  - Namespace `azure-dash-ztwim`, Route.
  - A `csi.spiffe.io` CSI volume for the agent socket.
  - ConfigMap `spiffe-helper` containing `helper.conf` (`agent_address`, `jwt_svids=[{jwt_audience="api://AzureADTokenExchange", jwt_svid_file_name=".../token"}]`, `cert_dir`). There are no shell templates, so the heredoc bug can't recur.
  - Init container: spiffe-helper with `daemon_mode=false`, so the token exists before the app starts.
  - Sidecar: spiffe-helper as a daemon for refresh. Image `registry.redhat.io/zero-trust-workload-identity-manager/spiffe-helper-rhel9`.
  - Shared `emptyDir` at `/var/run/secrets/azure`, mounted read-only in the app.
  - App env: `AUTH_MODE=spiffe`, `SPIFFE_JWT_FILE`, and `AZURE_CLIENT_ID`/`TENANT_ID` from a ConfigMap.
  - README:
    - required ZTWIM CR settings: `jwtKeyType: rsa-2048`, `SpireOIDCDiscoveryProvider` with `managedRoute` and a public TLS certificate
    - a `ClusterSPIFFEID` template
    - `curl` checks for `.well-known`, `/keys` (`use:sig`, `RS256`) and the token file format
    - a UAMI federated credential with subject `spiffe://<td>/ns/azure-dash-ztwim/sa/azure-dash`
    - Reader role assignment

**CI:** `.github/workflows/build-container.yaml` mirrors gcp-dash: amd64 and arm64 in a matrix, push by digest to ghcr.io, then merge into a manifest list, on the same triggers. A `dotnet test` job runs before the build; gcp-dash never ran its tests in CI. Dependabot covers nuget, docker and github-actions.

## Build order (the task plan expands each item, TDD throughout)

1. Solution scaffold, config, and the health and state endpoints.
2. Controls (liveness, readiness, CPU, crash).
3. Runtime info.
4. Layout and CSS, Runtime and Controls pages.
5. TtlCache and AzureService with FakeAzureProvider, plus the partial/JSON API.
6. CredentialFactory, SPIFFE assertion source, JWT display.
7. LiveAzureProvider (8 kinds) and subscription resolution.
8. Identity and IMDS panels.
9. Containerfile, `--healthcheck`, `run-podman.sh`.
10. Kustomize base and 4 overlays with READMEs.
11. CI and Dependabot.
12. Top-level README: demo script, security notes, troubleshooting.

## Verification

- `dotnet test`: every unit and integration test passes, using fakes; nothing calls Azure.
- Local run in `dev` mode: `az login`, then `dotnet run --project src/AzureDash`. Check that `/`, `/azure` (every panel either shows data or an explicit RBAC error), `/identity` (IMDS shows unavailable on a laptop) and the `/api/*` JSON respond, and that the controls flip the probes.
- `podman build --format docker -t azure-dash .` then `scripts/run-podman.sh` in client-secret mode. The HEALTHCHECK should report healthy.
- For each overlay: `kustomize build deploy/<overlay>` renders cleanly (`kubectl kustomize`).
- A live check on AKS with Workload ID. The Identity panel should show mode `workload-identity`, federated `sub=system:serviceaccount:azure-dash:azure-dash` and the ARM token's `oid`. The panels should load, and IMDS should show the MC_ node resource group. Run the demo script (readiness off → endpoints drop; liveness off → restart; CPU → `kubectl top`; crash → restart count).
- A live check on OpenShift ZTWIM. The Identity panel should show the SPIFFE ID as the federated `sub`, and panels should load after the JWT-SVID has rotated at least once (more than 5 minutes), which shows the callback re-reads the file. The cluster checks need real AKS and OpenShift environments; the build and render steps can be verified locally.
