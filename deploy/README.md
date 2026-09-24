# Deploying azure-dash

| Overlay | Platform | Credential | Secret in cluster? |
| --- | --- | --- | --- |
| [`aks/`](aks/README.md) | AKS | `WorkloadIdentityCredential` (projected SA token → Entra, via the Workload ID webhook) | No |
| [`openshift-wi/`](openshift-wi/README.md) | ARO / OCP with a public SA issuer | `WorkloadIdentityCredential` (token projected by the overlay) | No |
| [`ztwim/`](ztwim/README.md) | OpenShift + Zero Trust Workload Identity Manager | `ClientAssertionCredential` (SPIFFE JWT-SVID → Entra) | No |
| [`openshift/`](openshift/README.md) | Any Kubernetes/OpenShift | `ClientSecretCredential` | **Yes** (baseline for comparison) |

`kubernetes/` is the shared base: namespace `azure-dash`, ServiceAccount, ConfigMap `azure-dash-config`,
Deployment and Service on port 8080.

## Azure prerequisites shared by every overlay

1. **An identity.** Use a user-assigned managed identity for the federated overlays; it is single-tenant and
   managed with Azure RBAC. Use an app registration (service principal) for the client-secret overlay.
2. **Read access** for that identity:
   - `Reader` on the subscription, or
   - `Reader` on one resource group, with `AZURE_SUBSCRIPTION_ID` and `AZURE_RESOURCE_GROUP` set in the ConfigMap. Listing subscriptions returns nothing at resource-group scope.

   New role assignments can take up to 10 minutes to apply.
3. **A federated identity credential** (federated overlays only) whose issuer, subject and audience
   (`api://AzureADTokenExchange`) match the token the pod presents. An identity can hold up to 20 of these.

| Overlay | FIC issuer | FIC subject |
| --- | --- | --- |
| aks | `az aks show --query oidcIssuerProfile.issuerUrl` | `system:serviceaccount:azure-dash:azure-dash` |
| openshift-wi | `oc get authentication cluster -o jsonpath='{.spec.serviceAccountIssuer}'` | `system:serviceaccount:azure-dash:azure-dash` |
| ztwim | `oc get spireserver cluster -o jsonpath='{.spec.jwtIssuer}'` | `spiffe://<trust-domain>/ns/azure-dash-ztwim/sa/azure-dash` |
