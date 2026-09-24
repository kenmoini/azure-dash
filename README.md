# azure-dash

A small .NET 10 dashboard for demonstrating **Microsoft Entra Workload ID** on AKS and **federation from OpenShift**
(the cluster's own issuer, or Zero Trust Workload Identity Manager / SPIFFE). It is the Azure counterpart of
[gcp-dash](https://github.com/kenmoini/gcp-dash).

- **Runtime:** container, pod and cgroup info, plus controls to fail liveness or readiness, burn CPU, or crash the process.
- **Azure:** the subscription, resource groups, VMs and scale sets, storage accounts, VNets, subnets, NSG rules, and a Resource Graph count of resources by type. Each panel is cached and fails on its own.
- **Identity:** which credential the app chose and why, the decoded federated token the cluster presented (iss/sub/aud/exp), and the decoded Entra access token it got back (oid/tid/appid/xms_mirid). Raw tokens are never shown. It also shows **IMDS**: the node's VM metadata, when reachable.

## Authentication modes

`AUTH_MODE=auto` (the default) picks the first one that matches. `DefaultAzureCredential` is deliberately not used.

| Mode | Selected when | Credential |
| --- | --- | --- |
| `workload-identity` | `AZURE_FEDERATED_TOKEN_FILE` is set (AKS webhook, or the `openshift-wi` overlay) | `WorkloadIdentityCredential` |
| `spiffe` | `SPIFFE_JWT_FILE` is set (`ztwim` overlay) | `ClientAssertionCredential`, re-reading the JWT-SVID on every exchange |
| `client-secret` | `AZURE_CLIENT_SECRET` is set | `ClientSecretCredential` |
| `dev` | none of the above | Azure CLI, then Azure Developer CLI |

## Run locally

```bash
az login
dotnet test
ASPNETCORE_HTTP_PORTS=8080 dotnet run --project src/AzureDash    # http://localhost:8080
```

Container:

```bash
podman build --format docker -t localhost/azure-dash:dev .   # --format docker keeps the HEALTHCHECK
AZURE_TENANT_ID=... AZURE_CLIENT_ID=... AZURE_CLIENT_SECRET=... scripts/run-podman.sh
```

## Deploy

See [deploy/README.md](deploy/README.md). The overlays are `deploy/aks`, `deploy/openshift-wi`, `deploy/ztwim` and `deploy/openshift`.

## Endpoints

| Method | Path | Notes |
| --- | --- | --- |
| GET | `/`, `/azure`, `/identity` | Pages |
| GET | `/healthz/live`, `/healthz/ready` | `200 {"status":"ok"}`, or `503` when turned off from the controls |
| POST | `/controls/liveness`, `/controls/readiness` | Form `enabled=true\|false` |
| POST | `/controls/cpu` | Form `enabled`, `workers` (1–64, capped at the visible CPUs) |
| POST | `/controls/crash` | Form `exit_code` (0–255). Returns 202, then exits after 0.5 s. |
| GET | `/api/state`, `/api/runtime` | JSON |
| GET | `/api/azure/{kind}[?refresh=true]` | `kind` is one of `subscription`, `resourcegroups`, `vms`, `storageaccounts`, `vnets`, `subnets`, `nsgrules`, `resourcegraph`. Returns `{kind, fetched_at, error, items}`. |
| POST | `/api/azure/refresh` | Clears the cache (204) |
| GET | `/api/identity`, `/api/imds` | JSON |
| GET | `/partials/*` | htmx fragments |

Every `/controls/*` endpoint returns 403 when `CONTROLS_ENABLED=false`. Control and fragment endpoints return HTML to htmx (`HX-Request: true`) and JSON otherwise.

## Configuration

| Variable | Default | Meaning |
| --- | --- | --- |
| `AUTH_MODE` | `auto` | `auto`, `workload-identity`, `spiffe`, `client-secret` or `dev` |
| `AZURE_SUBSCRIPTION_ID` | — | Subscription to show. If unset, the app uses the only one it can see, or the first one plus a warning. |
| `AZURE_RESOURCE_GROUP` | — | Limit every panel to one resource group, for least-privilege Reader. In this mode the VM panel cannot show power state (the resource-group list API has no status-only option). |
| `AZURE_CLOUD` | `public` | `public`, `usgov` or `china`. Sets the authority host, the ARM endpoint and the token scope. |
| `AZURE_CACHE_TTL_SECONDS` | `60` | How long panel data is cached |
| `CONTROLS_ENABLED` | `true` | Set to `false` on shared clusters |
| `LOG_LEVEL` | `Information` | `Trace` … `None` |
| `AZURE_DEBUG` | `false` | Log Azure SDK HTTP, retry and token events. Authorization headers are redacted by Azure.Core. |
| `SPIFFE_JWT_FILE` | — | Path to the JWT-SVID written by spiffe-helper |
| `IMDS_ENABLED` / `IMDS_API_VERSION` | `true` / `2021-02-01` | Instance Metadata Service lookup (1 s timeout) |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE`, `AZURE_AUTHORITY_HOST`, `AZURE_CLIENT_SECRET` | — | Standard Azure Identity variables. The AKS webhook injects the first four. |
| `POD_NAME`, `POD_NAMESPACE`, `NODE_NAME`, `POD_IP`, `SERVICE_ACCOUNT` | — | Downward API, set by the base Deployment |

## Demo script

1. **Identity:** show the federated `iss`/`sub` and the Entra `oid`. There's no secret anywhere in the namespace.
2. **Azure:** the panels load. Remove the Reader role assignment and **Refresh all**: every panel shows a 403 with the "grant Reader" hint while keeping its last data. Restore the role; after propagation the panels recover.
3. **Readiness off:** `kubectl get endpoints azure-dash` drops the pod, and the Route or Ingress returns 503.
4. **Liveness off:** after about 15 s the kubelet restarts the container and the flags reset.
5. **CPU load:** `kubectl top pod`, or watch an HPA scale out.
6. **Crash** with exit code 3: `kubectl get pod` shows the restart count increase and `lastState.terminated.exitCode: 3`.

## Security

Everything is unauthenticated and the controls have no CSRF protection. That's fine for a demo, but don't expose
it publicly, set `CONTROLS_ENABLED=false` on shared clusters, and grant only `Reader`.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| `AADSTS700211` | The federated credential's issuer doesn't match the token's `iss`. Compare them on the Identity page. |
| `AADSTS700213` / `AADSTS70021` | The subject doesn't match: check namespace, service account or SPIFFE ID. A new FIC can take a few minutes to propagate. |
| `AuthorizationFailed (403)` | Reader is missing on the scope, or still propagating (up to about 10 minutes). |
| "No subscriptions are visible…" | Reader is scoped to a resource group. Set `AZURE_SUBSCRIPTION_ID` and `AZURE_RESOURCE_GROUP`. |
| Mode is `dev` in a cluster | Workload ID env wasn't injected: missing pod label, or the pod predates the annotation. Restart it. |
| IMDS unavailable | Not on an Azure VM, or AKS `--enable-imds-restriction`. This is expected. |

For more detail, run `kubectl -n azure-dash set env deploy/azure-dash AZURE_DEBUG=true` and read the logs.
