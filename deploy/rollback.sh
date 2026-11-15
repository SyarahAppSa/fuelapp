#!/bin/bash
# Manual rollback to an earlier version (its images must still exist: keep at least the last 3 versions).
#   deploy/rollback.sh                → back to the version before the current one
#   deploy/rollback.sh 1.0.2          → to a given version
#   deploy/rollback.sh 1.0.2 --restore-db backups/<folder> <age-private-key>
#        also restores the database and files from a backup (only if the newer version changed data in a way
#        the older one cannot read — see docs/RELEASE.md; data entered after that backup is lost)
set -uo pipefail
cd "$(dirname "$0")/.."
FILES="${COMPOSE_FILES:--f docker-compose.yml}"
TARGET="${1:-$(cat .deploy/previous 2>/dev/null)}"; [ -n "$TARGET" ] || { echo "no previous version recorded; pass one"; exit 1; }
echo "[$(date -u +%H:%M:%S)] manual rollback to $TARGET" | tee -a .deploy/history.log
if [ "${2:-}" = "--restore-db" ]; then
  B="${3:?backup folder}"; KEY="${4:?age private key file}"
  FUELUP_VERSION="$TARGET" docker compose $FILES stop api
  age -d -i "$KEY" "$B/db.dump.age" | docker compose $FILES exec -T db pg_restore -U postgres -d fuelup --clean --if-exists --exit-on-error || exit 1
  age -d -i "$KEY" "$B/files-keys.tar.gz.age" | FUELUP_VERSION="$TARGET" docker compose $FILES run --rm --no-deps -T --entrypoint "" -u root api sh -c 'rm -rf /data/files/* /data/keys/* && tar -C /data -xzf - && chown -R app:app /data' || exit 1
fi
FUELUP_VERSION="$TARGET" docker compose $FILES up -d --no-build
echo "$TARGET" > .deploy/current
echo "rolled back to $TARGET — check https://<your-domain>/health and sign in"
