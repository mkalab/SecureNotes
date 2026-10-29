#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

if [[ $# -ne 1 || ! "$1" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; then
    printf 'Usage: %s user@example.com\n' "$0" >&2
    exit 2
fi

if [[ ! -f .env ]]; then
    printf '%s\n' 'Missing .env; create it with ./scripts/init-dev-env.sh first.' >&2
    exit 1
fi

set -a
. ./.env
set +a
: "${NOTES_MIGRATOR_PASSWORD:?NOTES_MIGRATOR_PASSWORD is missing from .env}"

email="$1"
sql_server="${SECURENOTES_SQL_SERVER:-localhost,1433}"
query="EXEC dbo.PromoteUserToAdmin @Email = N'${email}';"
if command -v sqlcmd >/dev/null 2>&1; then
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" sqlcmd \
        -C -S "$sql_server" -d SecureNotes -U notes_migrator -b -Q "$query" >/dev/null
elif docker compose ps --status running db >/dev/null 2>&1; then
    docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -d SecureNotes -U notes_migrator -b -Q "$query" >/dev/null
else
    printf '%s\n' 'sqlcmd was not found and the Compose database is not running.' >&2
    exit 1
fi

printf 'Administrator role granted to %s. All existing sessions were revoked; sign in again with an authenticator code.\n' "$email"
