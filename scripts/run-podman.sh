#!/usr/bin/env bash
# Run azure-dash locally in podman using a service principal (client-secret mode):
#   AZURE_TENANT_ID=... AZURE_CLIENT_ID=... AZURE_CLIENT_SECRET=... [AZURE_SUBSCRIPTION_ID=...] scripts/run-podman.sh [image]
# Dev mode (az login) does not work inside the container because the image has no Azure CLI.
set -euo pipefail
IMAGE="${1:-localhost/azure-dash:dev}"
args=()
for v in AZURE_TENANT_ID AZURE_CLIENT_ID AZURE_CLIENT_SECRET AZURE_SUBSCRIPTION_ID AZURE_RESOURCE_GROUP AZURE_CLOUD AUTH_MODE LOG_LEVEL AZURE_DEBUG CONTROLS_ENABLED; do
  if [[ -n "${!v:-}" ]]; then args+=(-e "$v"); fi
done
exec podman run --rm -it --name azure-dash --cpus 1 --memory 512m -p 8080:8080 ${args[@]+"${args[@]}"} "$IMAGE"
