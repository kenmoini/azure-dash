# azure-dash on OpenShift (or any Kubernetes) with a client secret

The baseline to compare against Workload ID: a long-lived service principal secret stored in a Kubernetes Secret.
`AUTH_MODE=auto` selects `ClientSecretCredential` because `AZURE_CLIENT_SECRET` is present.

```bash
export SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az ad sp create-for-rbac --name azure-dash --role Reader --scopes "/subscriptions/$SUBSCRIPTION_ID" \
  --query '{AZURE_TENANT_ID:tenant, AZURE_CLIENT_ID:appId, AZURE_CLIENT_SECRET:password}' -o json
cp deploy/openshift/secret.example.yaml deploy/openshift/secret.yaml   # paste the three values; secret.yaml is gitignored
oc apply -k deploy/openshift
oc apply -f deploy/openshift/secret.yaml
oc -n azure-dash rollout restart deploy/azure-dash
oc -n azure-dash get route azure-dash -o jsonpath='https://{.spec.host}{"\n"}'
```

The Identity page shows mode `client-secret`, no federated token, and an Entra token whose `appid` is the service principal.
Rotate or delete the secret when you are done (`az ad sp delete --id <appId>`). Removing this secret is the
whole point of the Workload ID overlays.
