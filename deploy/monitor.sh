#!/bin/bash
# Server-side checks; run every 5 minutes from cron:  */5 * * * * cd /srv/fuelup && deploy/monitor.sh
# Sends a webhook alert (ALERT_WEBHOOK_URL in .env) on: not ready, disk > 85 %, TLS certificate < 21 days,
# last backup older than 26 h, or new unhandled server errors. Exit code ≠ 0 when something is wrong.
set -uo pipefail
cd "$(dirname "$0")/.."
HOST="${MONITOR_URL:-https://localhost}"
get() { grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2-; }
URL=$(get ALERT_WEBHOOK_URL); BDIR=$(get BACKUP_DIR); BDIR="${BDIR:-./backups}"
problems=()
ready=$(curl -sk -m 10 "$HOST/health/ready" || true)
echo "$ready" | grep -q '"status":"ready"' || problems+=("الخادم غير جاهز: ${ready:-لا يستجيب}")
disk=$(df -P / | awk 'NR==2 {gsub("%","",$5); print $5}'); [ "${disk:-0}" -gt 85 ] && problems+=("امتلاء القرص ${disk}%")
if [ -f deploy/certs/fullchain.pem ]; then
  end=$(openssl x509 -enddate -noout -in deploy/certs/fullchain.pem | cut -d= -f2); days=$(( ($(date -d "$end" +%s) - $(date +%s)) / 86400 ))
  [ "$days" -lt 21 ] && problems+=("شهادة TLS تنتهي خلال $days يومًا")
fi
last=$(ls -1d "$BDIR"/*/ 2>/dev/null | tail -1)
if [ -z "$last" ] || [ $(( $(date +%s) - $(stat -c %Y "$last") )) -gt 93600 ]; then problems+=("آخر نسخة احتياطية أقدم من 26 ساعة"); fi
errs=$(echo "$ready" | sed -n 's/.*"unhandledErrorsSinceStart":"\([0-9]*\)".*/\1/p'); prev=$(cat .deploy/errors 2>/dev/null || echo 0); mkdir -p .deploy
if [ -n "$errs" ]; then [ "$errs" -gt "$prev" ] && problems+=("$((errs - prev)) خطأ غير متوقع جديد في الخادم (التفاصيل في السجل)"); echo "$errs" > .deploy/errors; fi
if [ ${#problems[@]} -eq 0 ]; then echo "OK $(date -u +%FT%TZ)"; exit 0; fi
msg=$(printf '%s؛ ' "${problems[@]}")
echo "PROBLEM: $msg"
[ -n "$URL" ] && curl -s -m 5 -H 'content-type: application/json' -d "{\"text\":\"[FuelUp monitor] $msg\"}" "$URL" >/dev/null
exit 1
