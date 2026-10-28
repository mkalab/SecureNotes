#!/bin/sh
set -eu

if ! command -v openssl >/dev/null 2>&1; then
    printf '%s\n' 'OpenSSL is required to generate local database passwords.' >&2
    exit 1
fi

touch .env
chmod 600 .env

add_password_if_missing() {
    name="$1"
    if ! grep -q "^${name}=" .env; then
        password="Aa9!$(openssl rand -hex 24)"
        printf '%s=%s\n' "$name" "$password" >> .env
    fi
}

add_password_if_missing NOTES_MIGRATOR_PASSWORD
add_password_if_missing NOTES_APP_PASSWORD

if ! grep -q '^SECURENOTES_KEK=' .env; then
    key="$(openssl rand -base64 32 | tr -d '\n')"
    printf 'SECURENOTES_KEK=%s\n' "$key" >> .env
    unset key
fi
