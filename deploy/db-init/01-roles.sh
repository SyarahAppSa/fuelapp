#!/bin/sh
# Runs once, when the PostgreSQL volume is first created (as the superuser).
# Three accounts, least privilege:
#   postgres      superuser — administration only, never used by the application
#   fuelup_owner  owns the database and its tables — used only at start-up for migrations and hardening
#   fuelup_app    runtime account — no DDL, no DELETE (except where needed), cannot bypass row-level security
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" \
  -v owner_pw="$FUELUP_OWNER_PASSWORD" -v app_pw="$FUELUP_APP_PASSWORD" <<'SQL'
CREATE ROLE fuelup_owner LOGIN PASSWORD :'owner_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE fuelup_app   LOGIN PASSWORD :'app_pw'   NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE DATABASE fuelup OWNER fuelup_owner;
REVOKE ALL ON DATABASE fuelup FROM PUBLIC;
GRANT CONNECT ON DATABASE fuelup TO fuelup_app;
\connect fuelup
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO fuelup_owner;
SQL
