#!/bin/bash
# Deploys a new version with a safety net:
#   1. encrypted backup first (no backup → no deploy)
#   2. build (or pull) images tagged with the version
#   3. start the new version
#   4. smoke test: readiness, dashboard page, API answering, sign-in endpoint alive
#   5. if anything fails → automatic rollback to the previous version, and the failure is reported
# Usage: deploy/deploy.sh <version> [--pull | --prebuilt]      e.g. deploy/deploy.sh 1.0.3
#   --pull      images come from your registry (built by CI)     --prebuilt  images already loaded on this server
# Extra compose files (e.g. staging):  COMPOSE_FILES="-f docker-compose.yml -f docker-compose.staging.yml" deploy/deploy.sh 1.0.3
set -uo pipefail
cd "$(dirname "$0")/.."
NEW="${1:?version, e.g. 1.0.3}"; MODE="${2:-}"
FILES="${COMPOSE_FILES:--f docker-compose.yml}"
HOST="${SMOKE_URL:-https://localhost}"
STATE=.deploy; mkdir -p "$STATE"
PREV=$(cat "$STATE/current" 2>/dev/null || echo "")
dc() { FUELUP_VERSION="$1" docker compose $FILES "${@:2}"; }
log() { echo "[$(date -u +%H:%M:%S)] $*" | tee -a "$STATE/history.log"; }
alert() { url=$(grep -E '^ALERT_WEBHOOK_URL=' .env 2>/dev/null | cut -d= -f2-); [ -n "$url" ] && curl -s -m 5 -H 'content-type: application/json' -d "{\"text\":\"[FuelUp deploy] $1\"}" "$url" >/dev/null; true; }

smoke() {
  for i in $(seq 1 60); do curl -skf "$HOST/health/ready" >/dev/null 2>&1 && break; sleep 2; done
  local ready; ready=$(curl -sk "$HOST/health/ready")
  echo "$ready" | grep -q '"status":"ready"' || { log "smoke: not ready: $ready"; return 1; }
  echo "$ready" | grep -q "\"version\":\"$1\"" || { log "smoke: wrong version running: $ready"; return 1; }
  [ "$(curl -sk -o /dev/null -w '%{http_code}' "$HOST/")" = "200" ] || { log "smoke: dashboard page not served"; return 1; }
  [ "$(curl -sk -o /dev/null -w '%{http_code}' "$HOST/api/me")" = "401" ] || { log "smoke: API not answering as expected"; return 1; }
  [ "$(curl -sk -o /dev/null -w '%{http_code}' -H 'content-type: application/json' -d '{"username":"smoke-test-nobody","password":"x-smoke-x"}' "$HOST/api/auth/login")" = "401" ] || { log "smoke: sign-in endpoint not answering"; return 1; }
  return 0
}

log "deploy $NEW (previous: ${PREV:-none})"
if [ -n "$PREV" ]; then
  deploy/backup.sh || { log "backup FAILED — deploy aborted"; alert "backup failed, deploy of $NEW aborted"; exit 1; }
else
  log "first installation: no data to back up yet"
fi
case "$MODE" in
  --pull) dc "$NEW" pull api web || exit 1 ;;
  --prebuilt) for s in api web; do docker image inspect "${IMAGE_PREFIX:-fuelup}/$s:$NEW" >/dev/null 2>&1 || { log "image ${IMAGE_PREFIX:-fuelup}/$s:$NEW not found"; exit 1; }; done ;;
  *) dc "$NEW" build api web >/dev/null || { log "build failed"; exit 1; } ;;
esac
dc "$NEW" up -d --no-build
if smoke "$NEW"; then
  echo "$NEW" > "$STATE/current"; [ -n "$PREV" ] && echo "$PREV" > "$STATE/previous"
  log "deploy $NEW OK"
  exit 0
fi
log "deploy $NEW FAILED smoke test"
if [ -n "$PREV" ]; then
  log "rolling back to $PREV"
  dc "$PREV" up -d --no-build
  if smoke "$PREV"; then log "rollback to $PREV OK"; alert "deploy $NEW failed; automatically rolled back to $PREV"; else log "rollback FAILED — manual action needed (see docs/RELEASE.md)"; alert "deploy $NEW failed AND rollback to $PREV failed — manual action needed"; fi
fi
exit 2
