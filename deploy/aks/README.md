# azure-dash on AKS with Microsoft Entra Workload ID

The pod's projected service-account token (issued by the cluster's OIDC issuer) is exchanged with Entra for an
access token of a **user-assigned managed identity** (UAMI), using a **federated identity credential** (FIC).
The Workload ID webhook injects `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_FEDERATED_TOKEN_FILE` and
`AZURE_AUTHORITY_HOST` into pods labeled `azure.workload.identity/use: "true"`; azure-dash's `AUTH_MODE=auto`
then selects `WorkloadIdentityCredential`.

## 1. Enable the OIDC issuer and Workload ID

```bash
export RESOURCE_GROUP="rg-azure-dash"
export CLUSTER="my-aks-cluster"
export IDENTITY="id-azure-dash"
export SUBSCRIPTION_ID=$(az account show --query id -o tsv)

# For AKS Standard - AKS Automatic already has WID enabled
az aks update -g "$RESOURCE_GROUP" -n "$CLUSTER" --enable-oidc-issuer --enable-workload-identity

# Optional for AKS Standard, for the Ingress in this overlay:
az aks approuting enable -g "$RESOURCE_GROUP" -n "$CLUSTER"

# Get the AKS OIDC Issuer URI
export ISSUER=$(az aks show -g "$RESOURCE_GROUP" -n "$CLUSTER" --query oidcIssuerProfile.issuerUrl -o tsv)
```

## 2. Create the identity, federate it with the service account, and grant Reader

```bash
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
export PRINCIPAL_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)

az identity federated-credential create --name azure-dash \
  --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$ISSUER" \
  --subject system:serviceaccount:azure-dash:azure-dash \
  --audience api://AzureADTokenExchange

az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
  --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

For least privilege, scope Reader to a single resource group instead
(`--scope /subscriptions/$SUBSCRIPTION_ID/resourceGroups/<rg>`) and set both `AZURE_SUBSCRIPTION_ID` and
`AZURE_RESOURCE_GROUP` in `deploy/kubernetes/configmap.yaml` (or with `kubectl set env`).

## 3. Deploy

Put `$CLIENT_ID` into `deploy/aks/kustomization.yaml` **before** applying, so a later re-apply doesn't reset the
annotation back to zeros:

```bash
# Give yourself access to the AKS cluster if needed
AKS_ID=$(az aks show --resource-group $RESOURCE_GROUP --name $CLUSTER --query id --output tsv)
az role assignment create --role "Azure Kubernetes Service RBAC Cluster Admin" --assignee "you@domain.com" --scope $AKS_ID

# Log into the AKS Cluster
az aks get-credentials -g "$RESOURCE_GROUP" -n "$CLUSTER"

# Replace variable placeholders
sed -i.bak "s/00000000-0000-0000-0000-000000000000/$CLIENT_ID/" deploy/aks/kustomization.yaml
sed -i.bak "s/AZ_SUB_ID/$SUBSCRIPTION_ID/" deploy/aks/kustomization.yaml
sed -i.bak "s/AZ_RG_NAME/$RESOURCE_GROUP/" deploy/aks/kustomization.yaml

# Deploy the application
kubectl apply -k deploy/aks

# Watch the rollout, AKS Auto may take a minute to scale up a node
kubectl -n azure-dash rollout status deploy/azure-dash

# Ensure the injected environment variables are set
kubectl -n azure-dash exec deploy/azure-dash -- printenv | grep ^AZURE_

# Access the application UI
kubectl -n azure-dash get ingress azure-dash   # browse to the ADDRESS, or: kubectl -n azure-dash port-forward svc/azure-dash 8080
```

If pods already existed in the namespace before the client-id annotation and `azure.workload.identity/use` label
were set (for example, you applied this overlay once already with the placeholder GUID), the Workload ID webhook
never had a chance to mutate them: `kubectl -n azure-dash rollout restart deploy/azure-dash` after the apply above
picks up the annotation on fresh pods.

Open **Identity**. You should see:
- mode `workload-identity`
- federated token `sub = system:serviceaccount:azure-dash:azure-dash`, with `iss` = `$ISSUER`
- an Entra access token whose `xms_mirid` names `id-azure-dash`

**IMDS** shows the node's VM scale set in the `MC_…` resource group.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity record) | The FIC subject must be exactly `system:serviceaccount:azure-dash:azure-dash`, and the issuer must match `$ISSUER`, including the trailing slash. A new FIC can take a few minutes to propagate. |
| Mode is `dev`, not `workload-identity` | The pod was created before the label/annotation existed. Run `kubectl rollout restart`. Check `kubectl get pod -o yaml` for the injected `AZURE_*` env. |
| `AuthorizationFailed (403)` in panels | The Reader role assignment is missing or still propagating (up to about 10 minutes). |
| IMDS unavailable | Expected if the cluster uses `--enable-imds-restriction`. |
