#!/bin/bash
# Proves a backup can actually be restored: decrypts it into a throw-away PostgreSQL container and checks it.
# Usage: deploy/restore-test.sh <backup-folder> <age-private-key-file>
# Run monthly (and after any change to the database), keep the output as evidence.
set -euo pipefail
B="${1:?backup folder}"; KEY="${2:?age private key file}"
NAME="fuelup-restore-test-$$"; PW=$(openssl rand -hex 16); fail=0
check() { if eval "$2"; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi; }
cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

(cd "$B" && sha256sum -c --quiet SHA256SUMS) && echo "PASS checksums match (files not altered since backup)" || { echo "FAIL checksums"; exit 1; }
docker run -d --name "$NAME" -e POSTGRES_PASSWORD="$PW" postgres:16-alpine >/dev/null
for i in $(seq 1 60); do docker exec "$NAME" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 1; done
docker exec "$NAME" psql -qU postgres -c "CREATE ROLE fuelup_owner NOLOGIN" -c "CREATE ROLE fuelup_app NOLOGIN" -c "CREATE DATABASE fuelup OWNER fuelup_owner" >/dev/null
age -d -i "$KEY" "$B/db.dump.age" | docker exec -i "$NAME" pg_restore -U postgres -d fuelup --exit-on-error
q() { docker exec "$NAME" psql -U postgres -d fuelup -tAc "$1"; }

while read -r table expected; do
  got=$(q "select count(*) from $table")
  check "$table rows restored: $got (backup recorded $expected)" "[ '$got' = '$expected' ]"
done < "$B/counts.txt"
rls=$(q "select count(*) from pg_class where relrowsecurity and relforcerowsecurity")
trg=$(q "select count(*) from pg_trigger where tgname = 'append_only'")
mig=$(q 'select count(*) from "__EFMigrationsHistory"')
check "row-level security still enabled on company tables ($rls)" "[ ${rls:-0} -ge 30 ]"
check "append-only triggers present on audit and meter_readings" "[ ${trg:-0} -eq 2 ]"
check "migration history present (${mig:-0} migrations)" "[ ${mig:-0} -ge 1 ]"

entries=$(age -d -i "$KEY" "$B/files-keys.tar.gz.age" | tar -tzf - | wc -l)
check "files + Data Protection keys archive readable ($entries entries)" "age -d -i '$KEY' '$B/files-keys.tar.gz.age' | tar -tzf - | grep -q '^keys/key-'"

[ $fail -eq 0 ] && echo "RESTORE TEST PASSED" || { echo "RESTORE TEST FAILED"; exit 1; }
