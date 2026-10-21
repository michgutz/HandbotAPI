#!/usr/bin/env bash
# One-time setup so GitHub Actions can deploy HandbotAPI without a service account key
# (Workload Identity Federation). Run ./deploy.sh once first: it creates the Artifact
# Registry repo and the Cloud Run runtime service account this script relies on.
#
# Usage:
#   PROJECT_ID=my-project ./setup-cicd.sh
#
# Optional:
#   GITHUB_REPO   owner/repo allowed to deploy  (default: michgutz/HandbotAPI)
#   SERVICE_NAME  Cloud Run service name        (default: handbot-api)
#
# At the end it prints the values the workflow uses (only needed as GitHub repository
# variables if they differ from the defaults in .github/workflows/ci-cd.yml).

set -euo pipefail

: "${PROJECT_ID:?Set PROJECT_ID to the project the app is deployed into}"
GITHUB_REPO="${GITHUB_REPO:-michgutz/HandbotAPI}"
SERVICE_NAME="${SERVICE_NAME:-handbot-api}"

POOL_ID="github"
PROVIDER_ID="github"
DEPLOYER_NAME="github-deployer"
DEPLOYER_SA="${DEPLOYER_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"
RUNTIME_SA="${SERVICE_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"
PROJECT_NUMBER="$(gcloud projects describe "${PROJECT_ID}" --format 'value(projectNumber)')"

# A newly created service account can take a little while to be usable in IAM bindings.
retry() {
  local attempt
  for attempt in 1 2 3 4 5 6; do
    "$@" && return 0
    echo "    retrying in 10s (attempt ${attempt}/6)..." >&2
    sleep 10
  done
  "$@"
}

echo "==> Enabling APIs needed for Workload Identity Federation"
gcloud services enable iamcredentials.googleapis.com sts.googleapis.com --project "${PROJECT_ID}"

echo "==> Ensuring deployer service account ${DEPLOYER_SA}"
if ! gcloud iam service-accounts describe "${DEPLOYER_SA}" --project "${PROJECT_ID}" >/dev/null 2>&1; then
  gcloud iam service-accounts create "${DEPLOYER_NAME}" \
    --display-name "GitHub Actions deployer for ${GITHUB_REPO}" \
    --project "${PROJECT_ID}"
fi

echo "==> Granting deployer roles"
for role in roles/run.admin roles/artifactregistry.writer; do
  retry gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
    --member "serviceAccount:${DEPLOYER_SA}" \
    --role "${role}" \
    --condition None \
    >/dev/null
done
# Lets the deployer attach the runtime service account to the Cloud Run service (and nothing else).
retry gcloud iam service-accounts add-iam-policy-binding "${RUNTIME_SA}" \
  --member "serviceAccount:${DEPLOYER_SA}" \
  --role roles/iam.serviceAccountUser \
  --project "${PROJECT_ID}" \
  >/dev/null

echo "==> Ensuring workload identity pool and GitHub provider"
if ! gcloud iam workload-identity-pools describe "${POOL_ID}" --location global --project "${PROJECT_ID}" >/dev/null 2>&1; then
  gcloud iam workload-identity-pools create "${POOL_ID}" \
    --location global \
    --display-name "GitHub Actions" \
    --project "${PROJECT_ID}"
fi
if ! gcloud iam workload-identity-pools providers describe "${PROVIDER_ID}" --workload-identity-pool "${POOL_ID}" --location global --project "${PROJECT_ID}" >/dev/null 2>&1; then
  gcloud iam workload-identity-pools providers create-oidc "${PROVIDER_ID}" \
    --workload-identity-pool "${POOL_ID}" \
    --location global \
    --issuer-uri "https://token.actions.githubusercontent.com" \
    --attribute-mapping "google.subject=assertion.sub,attribute.repository=assertion.repository" \
    --attribute-condition "assertion.repository == '${GITHUB_REPO}'" \
    --project "${PROJECT_ID}"
fi

echo "==> Allowing ${GITHUB_REPO} to impersonate the deployer"
retry gcloud iam service-accounts add-iam-policy-binding "${DEPLOYER_SA}" \
  --member "principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_ID}/attribute.repository/${GITHUB_REPO}" \
  --role roles/iam.workloadIdentityUser \
  --project "${PROJECT_ID}" \
  >/dev/null

echo
echo "Done. The workflow defaults match massive-incline-462117-n7 / us-central1. If you deployed elsewhere,"
echo "override them with these GitHub repository variables (Settings > Secrets and variables > Actions > Variables):"
echo "  GCP_PROJECT_ID = ${PROJECT_ID}"
echo "  GCP_REGION     = <the region you passed to deploy.sh>"
echo "  WIF_PROVIDER   = projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_ID}/providers/${PROVIDER_ID}"
echo "  DEPLOYER_SA    = ${DEPLOYER_SA}"
