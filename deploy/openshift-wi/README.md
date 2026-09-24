# azure-dash on OpenShift using the cluster's own service-account issuer

Same mechanism as AKS Workload ID, using OpenShift's service-account issuer instead of AKS's: the pod gets a
projected token with audience `api://AzureADTokenExchange`, and Entra trusts it through a federated identity
credential. azure-dash runs `WorkloadIdentityCredential` (`AUTH_MODE=auto` sees `AZURE_FEDERATED_TOKEN_FILE`).
This overlay projects the token itself, so it does not depend on the pod-identity webhook.

**Requirement:** Entra must be able to fetch the issuer's discovery document and JWKS over public HTTPS.

## 1. Find the issuer and check it is publicly discoverable

```bash
export ISSUER=$(oc get authentication cluster -o jsonpath='{.spec.serviceAccountIssuer}')
echo "$ISSUER"
curl -fsS "$ISSUER/.well-known/openid-configuration" | jq '{issuer, jwks_uri}'
curl -fsS "$(curl -fsS "$ISSUER/.well-known/openid-configuration" | jq -r .jwks_uri)" | jq '.keys[] | {kty, alg, use}'
```

- **Azure Red Hat OpenShift (ARO)** with managed/workload identity: the issuer is `https://<region>.oic.aro.azure.net/...` and is public.
- **Self-managed OCP installed in Manual/STS mode with `ccoctl azure create-all`**: the issuer points to a public Azure Blob container.
- **Default OCP install**: `serviceAccountIssuer` is empty, which means `https://kubernetes.default.svc`. That isn't public, so Entra can't use it. Use the ZTWIM overlay (`deploy/ztwim`) instead, or re-issue with `ccoctl`.

## 2. Identity, federated credential, Reader

```bash
export RESOURCE_GROUP=rg-azure-dash IDENTITY=id-azure-dash-ocp
export SUBSCRIPTION_ID=$(az account show --query id -o tsv) TENANT_ID=$(az account show --query tenantId -o tsv)
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
az identity federated-credential create --name azure-dash-ocp --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$ISSUER" --subject system:serviceaccount:azure-dash:azure-dash --audience api://AzureADTokenExchange
az role assignment create --assignee-object-id "$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)" \
  --assignee-principal-type ServicePrincipal --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

## 3. Deploy

Put `$CLIENT_ID` and `$TENANT_ID` into `deploy/openshift-wi/identity-configmap.yaml`, then:

```bash
oc apply -k deploy/openshift-wi
oc -n azure-dash rollout status deploy/azure-dash
oc -n azure-dash get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page should show mode `workload-identity`, with a federated `iss` equal to `$ISSUER` and
`sub = system:serviceaccount:azure-dash:azure-dash`. If Entra rejects the token (`AADSTS700211`, no matching
federated identity record for the issuer), the issuer string in the FIC doesn't exactly match the token's `iss`.
