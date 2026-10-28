#!/bin/sh
set -eu

if [ -e .env ]; then
    ./scripts/add-dev-db-credentials.sh
    exit 0
fi

if ! command -v openssl >/dev/null 2>&1; then
    printf '%s\n' 'OpenSSL is required to generate the local SQL Server password.' >&2
    exit 1
fi

password="$(openssl rand -hex 24)Aa1!"
(umask 077 && printf 'MSSQL_SA_PASSWORD=%s\n' "$password" > .env)
printf '%s\n' 'Created .env with a random local SQL Server password (file mode 600).'
./scripts/add-dev-db-credentials.sh
