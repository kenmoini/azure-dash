# azure-dash on OpenShift with Zero Trust Workload Identity Manager (SPIFFE/SPIRE) → Azure

SPIRE issues the pod a **JWT-SVID** whose `sub` is its SPIFFE ID. Entra trusts SPIRE's OIDC discovery endpoint
through a federated identity credential, and exchanges the JWT-SVID for an access token.

- spiffe-helper (Red Hat image) fetches the JWT-SVID over the SPIRE agent socket. The socket is mounted with the `csi.spiffe.io` CSI driver.
- spiffe-helper writes the JWT-SVID to `/var/run/secrets/azure/token`.
- azure-dash uses `ClientAssertionCredential` and **re-reads that file on every token exchange**. ZTWIM's default JWT-SVID lifetime is about 5 minutes, so a cached copy would expire.

## 0. ZTWIM prerequisites Entra enforces

The Zero Trust Workload Identity Manager operator must be installed, with its `cluster` CRs created. Entra has three requirements:

1. **RS256 signing.** Entra only accepts RSA-signed tokens, while upstream SPIRE defaults to EC keys:
   ```yaml
   apiVersion: operator.openshift.io/v1alpha1
   kind: SpireServer
   metadata: {name: cluster}
   spec:
     jwtIssuer: https://oidc-discovery.apps.<cluster-domain>
     jwtKeyType: rsa-2048
     # ...
   ```
2. **Public discovery endpoint.** `SpireOIDCDiscoveryProvider` must be reachable from the internet over HTTPS with a **publicly trusted** certificate, for example `managedRoute: "true"` plus a certificate from cert-manager/ACME via `externalSecretRef`. Its `jwtIssuer` must equal the SpireServer's.
3. **Key usage.** The keys in the JWKS must carry `"use": "sig"`.

Check all three:

```bash
export JWT_ISSUER=$(oc get spireserver cluster -o jsonpath='{.spec.jwtIssuer}')
export TRUST_DOMAIN=$(oc get zerotrustworkloadidentitymanager cluster -o jsonpath='{.spec.trustDomain}')
curl -fsS "$JWT_ISSUER/.well-known/openid-configuration" | jq '{issuer, jwks_uri, id_token_signing_alg_values_supported}'
curl -fsS "$(curl -fsS "$JWT_ISSUER/.well-known/openid-configuration" | jq -r .jwks_uri)" | jq '.keys[] | {kty, alg, use, kid}'
```

**Expect `kty: "RSA"` and `use: "sig"`.** Run these from outside the cluster to prove the endpoint is public.

Workloads need a SPIFFE ID of the form `spiffe://<trust-domain>/ns/<namespace>/sa/<service-account>`. Check which `ClusterSPIFFEID` resources exist with `oc get clusterspiffeid`. If none matches, create one:

```yaml
apiVersion: spire.spiffe.io/v1alpha1
kind: ClusterSPIFFEID
metadata: {name: azure-dash}
spec:
  spiffeIDTemplate: "spiffe://{{ .TrustDomain }}/ns/{{ .PodMeta.Namespace }}/sa/{{ .PodSpec.ServiceAccountName }}"
  namespaceSelector:
    matchLabels: {kubernetes.io/metadata.name: azure-dash-ztwim}
```

## 1. Identity, federated credential, Reader

```bash
export RESOURCE_GROUP=rg-azure-dash IDENTITY=id-azure-dash-ztwim
export SUBSCRIPTION_ID=$(az account show --query id -o tsv) TENANT_ID=$(az account show --query tenantId -o tsv)
az identity create -g "$RESOURCE_GROUP" -n "$IDENTITY"
export CLIENT_ID=$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query clientId -o tsv)
az identity federated-credential create --name azure-dash-ztwim --identity-name "$IDENTITY" -g "$RESOURCE_GROUP" \
  --issuer "$JWT_ISSUER" \
  --subject "spiffe://$TRUST_DOMAIN/ns/azure-dash-ztwim/sa/azure-dash" \
  --audience api://AzureADTokenExchange
az role assignment create --assignee-object-id "$(az identity show -g "$RESOURCE_GROUP" -n "$IDENTITY" --query principalId -o tsv)" \
  --assignee-principal-type ServicePrincipal --role Reader --scope "/subscriptions/$SUBSCRIPTION_ID"
```

## 2. Deploy

Put `$CLIENT_ID` and `$TENANT_ID` into `deploy/ztwim/identity-configmap.yaml`, then:

```bash
oc apply -k deploy/ztwim
oc -n azure-dash-ztwim rollout status deploy/azure-dash
oc -n azure-dash-ztwim logs deploy/azure-dash -c spiffe-helper --tail=20
oc -n azure-dash-ztwim exec deploy/azure-dash -c azure-dash -- head -c 20 /var/run/secrets/azure/token; echo   # expect: eyJ...
oc -n azure-dash-ztwim get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page should show:
- mode `spiffe`, credential `ClientAssertionCredential`
- a federated `sub` of `spiffe://<trust-domain>/ns/azure-dash-ztwim/sa/azure-dash` and an `iss` of `$JWT_ISSUER`
- an Entra token whose `xms_mirid` names `id-azure-dash-ztwim`

Reload the **Identity** page twice, a few minutes apart, and compare the federated token's `iat`/`exp`: they
advance between loads, which shows spiffe-helper rotating the JWT-SVID on disk and `IdentityInfoService`
re-reading the file on every load. Clicking **Refresh all** alone doesn't prove the *token exchange* is repeated —
it only clears the app's in-memory cache, while `ClientAssertionCredential`/MSAL still caches the Entra access
token for its own lifetime (about 60–90 minutes). To prove the callback re-reads the file for a genuine new
exchange, wait until the Entra access token's `exp` shown on the Identity page has passed, then click
**Refresh all** on the Azure page: the panels still load, which means a new token exchange happened using the
JWT-SVID that's current at that moment (not a cached access token).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `SPIFFE JWT-SVID file not found … spiffe-helper` | The init container failed. Check `oc logs deploy/azure-dash -c spiffe-helper-init`. The CSI driver or the `ClusterSPIFFEID` is missing. |
| `AADSTS700211` (no matching federated identity record for the issuer) | The FIC `--issuer` must exactly equal the token's `iss`, i.e. `$JWT_ISSUER`, including scheme and trailing slash. |
| `AADSTS700213` (… for the subject) | The FIC `--subject` must equal the SPIFFE ID shown on the Identity page. |
| Signature or key errors | `jwtKeyType` is not RSA, or the JWKS lacks `use: sig`. Fix the SpireServer CR, then wait for key rotation. |
| Entra can't fetch the keys | The discovery route isn't public, or its certificate isn't publicly trusted. |
