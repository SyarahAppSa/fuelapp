#!/bin/sh
# The platform mounts the persistent disk at /data owned by root: give it to the app user, then run the API
# WITHOUT root privileges (setpriv drops user, group and every capability).
set -e
mkdir -p /data/files /data/keys
chown -R app:app /data
exec setpriv --reuid=app --regid=app --init-groups --inh-caps=-all --bounding-set=-all dotnet /app/FuelUp.Api.dll
