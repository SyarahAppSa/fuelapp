#!/bin/bash
# Encrypted backup of the database AND the files/keys volumes.
#   • Encrypted with age (https://age-encryption.org) to a PUBLIC key: this server can create backups but cannot read them.
#     The private key stays offline (password manager / safe). Losing it = losing the backups.
#   • The Data Protection keys volume is included: without it, two-step-verification secrets cannot be decrypted after a restore.
# Schedule daily (cron/systemd timer), copy the output to a second location (other region / offline disk), and run
# deploy/restore-test.sh at least monthly.
set -euo pipefail
cd "$(dirname "$0")/.."
get() { grep -E "^$1=" .env | tail -1 | cut -d= -f2-; }
RECIPIENT="${BACKUP_AGE_RECIPIENT:-$(get BACKUP_AGE_RECIPIENT)}"
DIR="${BACKUP_DIR:-$(get BACKUP_DIR)}"; DIR="${DIR:-./backups}"
KEEP="${BACKUP_KEEP_DAYS:-$(get BACKUP_KEEP_DAYS)}"; KEEP="${KEEP:-30}"
[ -n "$RECIPIENT" ] || { echo "BACKUP_AGE_RECIPIENT is not set (age public key, age1...)" >&2; exit 1; }
command -v age >/dev/null || { echo "install age first (apt install age)" >&2; exit 1; }

TS=$(date -u +%Y%m%dT%H%M%SZ); OUT="$DIR/$TS"; mkdir -p "$OUT"; chmod 700 "$DIR" "$OUT"
umask 077

docker compose exec -T db pg_dump -U postgres -d fuelup -Fc | age -r "$RECIPIENT" > "$OUT/db.dump.age"
docker compose exec -T api tar -C /data -czf - files keys | age -r "$RECIPIENT" > "$OUT/files-keys.tar.gz.age"
# row counts at backup time (not sensitive) so a restore can be verified against them
docker compose exec -T db psql -U postgres -d fuelup -tA -F' ' -c \
  "select 'companies', count(*) from companies union all select 'users', count(*) from users union all select 'trips', count(*) from trips
   union all select 'meter_readings', count(*) from meter_readings union all select 'audit', count(*) from audit union all select 'attachments', count(*) from attachments" > "$OUT/counts.txt"
(cd "$OUT" && sha256sum db.dump.age files-keys.tar.gz.age counts.txt > SHA256SUMS)

# empty or truncated output = failed backup, never silently "succeeded"
[ "$(stat -c%s "$OUT/db.dump.age")" -gt 1000 ] || { echo "backup FAILED: database dump too small" >&2; exit 2; }
find "$DIR" -mindepth 1 -maxdepth 1 -type d -mtime +"$KEEP" -exec rm -rf {} +
echo "backup OK: $OUT"
