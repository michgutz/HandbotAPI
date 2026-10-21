#!/usr/bin/env bash
# Builds HandbotAPI with Cloud Build and deploys it to Cloud Run.
#
# Usage:
#   PROJECT_ID=my-project REGION=us-central1 ./deploy.sh
#
# Optional:
#   SERVICE_NAME          Cloud Run service name        (default: handbot-api)
#   AR_REPO               Artifact Registry repo name   (default: handbot)
#   DATASTORE_PROJECT_ID  Project that owns the Vertex AI Search data store
#                         (default: massive-incline-462117-n7)
#   GRANT_DATASTORE_ROLE  Set to "false" to skip granting the service account access
#                         to the data store (e.g. if the data store owner grants it).
#   RUNTIME_SA            Use an existing service account (email) for Cloud Run instead of
#                         creating one; also skips the data store grant.
#   LOCAL_BUILD           Set to "true" to build with local docker buildx and push to
#                         Artifact Registry instead of using Cloud Build.
#
# The caller needs permission to enable APIs, create the Artifact Registry repo and
# service account, run Cloud Build, deploy Cloud Run, and (unless skipped) set IAM
# policy on DATASTORE_PROJECT_ID.

set -euo pipefail

: "${PROJECT_ID:?Set PROJECT_ID to the project to deploy into}"
: "${REGION:?Set REGION, e.g. us-central1}"
SERVICE_NAME="${SERVICE_NAME:-handbot-api}"
AR_REPO="${AR_REPO:-handbot}"
DATASTORE_PROJECT_ID="${DATASTORE_PROJECT_ID:-massive-incline-462117-n7}"
GRANT_DATASTORE_ROLE="${GRANT_DATASTORE_ROLE:-true}"

SA_NAME="${SERVICE_NAME}"
SA_EMAIL="${RUNTIME_SA:-${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com}"
IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${AR_REPO}/${SERVICE_NAME}:$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)"

cd "$(dirname "$0")"

echo "==> Enabling required APIs in ${PROJECT_ID}"
gcloud services enable \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  artifactregistry.googleapis.com \
  --project "${PROJECT_ID}"

echo "==> Ensuring Artifact Registry repo ${AR_REPO}"
if ! gcloud artifacts repositories describe "${AR_REPO}" --location "${REGION}" --project "${PROJECT_ID}" >/dev/null 2>&1; then
  gcloud artifacts repositories create "${AR_REPO}" \
    --repository-format docker \
    --location "${REGION}" \
    --project "${PROJECT_ID}"
fi

if [[ -z "${RUNTIME_SA:-}" ]]; then
  echo "==> Ensuring service account ${SA_EMAIL}"
  if ! gcloud iam service-accounts describe "${SA_EMAIL}" --project "${PROJECT_ID}" >/dev/null 2>&1; then
    gcloud iam service-accounts create "${SA_NAME}" \
      --display-name "HandbotAPI Cloud Run runtime" \
      --project "${PROJECT_ID}"
  fi
fi

if [[ -z "${RUNTIME_SA:-}" && "${GRANT_DATASTORE_ROLE}" == "true" ]]; then
  echo "==> Granting Discovery Engine Viewer on ${DATASTORE_PROJECT_ID} to ${SA_EMAIL}"
  gcloud projects add-iam-policy-binding "${DATASTORE_PROJECT_ID}" \
    --member "serviceAccount:${SA_EMAIL}" \
    --role roles/discoveryengine.viewer \
    --condition None \
    >/dev/null
fi

echo "==> Building ${IMAGE}"
if [[ "${LOCAL_BUILD:-false}" == "true" ]]; then
  gcloud auth configure-docker "${REGION}-docker.pkg.dev" --quiet
  docker buildx build --platform linux/amd64 -t "${IMAGE}" --push .
else
  gcloud builds submit . --tag "${IMAGE}" --project "${PROJECT_ID}"
fi

echo "==> Deploying ${SERVICE_NAME} to Cloud Run (${REGION})"
gcloud run deploy "${SERVICE_NAME}" \
  --image "${IMAGE}" \
  --region "${REGION}" \
  --project "${PROJECT_ID}" \
  --service-account "${SA_EMAIL}" \
  --port 8080 \
  --no-allow-unauthenticated \
  --set-env-vars "VertexAiSearch__ProjectId=${DATASTORE_PROJECT_ID}"

URL="$(gcloud run services describe "${SERVICE_NAME}" --region "${REGION}" --project "${PROJECT_ID}" --format 'value(status.url)')"
echo
echo "Deployed: ${URL}"
echo "Test with:"
echo "  curl -H \"Authorization: Bearer \$(gcloud auth print-identity-token)\" \"${URL}/KnowledgeBase/search?query=vacation\""
