#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ ! -f .env ]]; then
    printf '%s\n' 'Missing .env; create it before configuring SQL Server Audit.' >&2
    exit 1
fi

set -a
. ./.env
set +a

: "${SECURENOTES_SQL_ADMIN_USER:?Set SECURENOTES_SQL_ADMIN_USER to an existing SQL Server sysadmin login; sa is disabled by default.}"
: "${SECURENOTES_SQL_ADMIN_PASSWORD:?Set SECURENOTES_SQL_ADMIN_PASSWORD for that sysadmin login.}"

docker compose exec -T -e "SQLCMDPASSWORD=$SECURENOTES_SQL_ADMIN_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U "$SECURENOTES_SQL_ADMIN_USER" -d master -b \
    < database/009_p9_xevents.sql

printf '%s\n' 'SQL Server Extended Events enabled for failed logins and DDL; the session starts with SQL Server.'
