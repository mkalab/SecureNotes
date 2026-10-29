#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

if ! command -v sqlcmd >/dev/null 2>&1; then
    printf '%s\n' 'Install Microsoft sqlcmd tools and add sqlcmd to PATH.' >&2
    exit 1
fi

if [[ ! -f .env ]]; then
    printf '%s\n' 'Missing .env. Copy .env.example to .env and run ./scripts/init-dev-env.sh first.' >&2
    exit 1
fi

set -a
. ./.env
set +a

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is missing from .env}"
: "${NOTES_MIGRATOR_PASSWORD:?NOTES_MIGRATOR_PASSWORD is missing from .env}"
: "${NOTES_APP_PASSWORD:?NOTES_APP_PASSWORD is missing from .env}"

sql_admin_user="${SECURENOTES_SQL_ADMIN_USER:-sa}"
sql_admin_password="${SECURENOTES_SQL_ADMIN_PASSWORD:-$MSSQL_SA_PASSWORD}"
sql_server="${SECURENOTES_SQL_SERVER:-localhost,1433}"
schema_file="$(mktemp)"
trap 'rm -f "$schema_file"' EXIT

sqlcmd_file_path() {
    case "$(uname -s)" in
        MINGW*|MSYS*|CYGWIN*)
            cygpath -w "$1"
            ;;
        *)
            printf '%s\n' "$1"
            ;;
    esac
}

while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line//__NOTES_MIGRATOR_PASSWORD__/$NOTES_MIGRATOR_PASSWORD}"
    line="${line//__NOTES_APP_PASSWORD__/$NOTES_APP_PASSWORD}"
    printf '%s\n' "$line" >> "$schema_file"
done < database/001_schema.sql

schema_sql_file="$(sqlcmd_file_path "$schema_file")"
SQLCMDPASSWORD="$sql_admin_password" sqlcmd \
    -C -S "$sql_server" -d master -U "$sql_admin_user" -b -i "$schema_sql_file"

for migration in database/002_p2.sql database/003_p3.sql database/004_p4.sql database/005_p5.sql database/006_p6.sql database/007_p7.sql database/008_p8.sql; do
    migration_sql_file="$(sqlcmd_file_path "$migration")"
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" sqlcmd \
        -C -S "$sql_server" -d SecureNotes -U notes_migrator -b -i "$migration_sql_file"
done

if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" sqlcmd \
    -C -S "$sql_server" -d SecureNotes -U notes_app -b \
    -Q "SELECT TOP (1) UserId FROM dbo.users;" >/dev/null 2>&1
then
    printf '%s\n' 'Database permission check failed: notes_app can read dbo.users directly.' >&2
    exit 1
fi

SQLCMDPASSWORD="$NOTES_APP_PASSWORD" sqlcmd \
    -C -S "$sql_server" -d SecureNotes -U notes_app -b \
    -Q "EXEC app.GetUserByEmail @Email = N'local-db-permission-check@invalid.local';" >/dev/null

printf 'Local SQL Server database is ready at %s.\n' "$sql_server"
