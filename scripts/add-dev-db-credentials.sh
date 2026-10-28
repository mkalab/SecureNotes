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
    if grep -q "^${name}=." .env; then
        return
    fi

    password="$(openssl rand -hex 24)Aa1!"
    if grep -q "^${name}=" .env; then
        sed -i "s|^${name}=$|${name}=${password}|" .env
    else
        printf '%s=%s\n' "$name" "$password" >> .env
    fi
    unset password
}

add_missing_template_settings() {
    if [ ! -f .env.example ]; then
        printf '%s\n' 'Missing .env.example; cannot add optional local settings.' >&2
        exit 1
    fi

    added_settings=0
    while IFS= read -r line || [ -n "$line" ]; do
        case "$line" in
            ''|\#*) continue ;;
        esac

        name="${line%%=*}"
        if ! grep -q "^${name}=" .env; then
            if [ "$added_settings" -eq 0 ]; then
                printf '\n%s\n' '# Optional settings from .env.example; edit values if needed.' >> .env
                added_settings=1
            fi
            printf '%s\n' "$line" >> .env
        fi
    done < .env.example
}

add_missing_template_settings
add_password_if_missing MSSQL_SA_PASSWORD
add_password_if_missing NOTES_MIGRATOR_PASSWORD
add_password_if_missing NOTES_APP_PASSWORD

if ! grep -q '^SECURENOTES_KEK=.' .env; then
    key="$(openssl rand -base64 32 | tr -d '\n')"
    if grep -q '^SECURENOTES_KEK=' .env; then
        sed -i "s|^SECURENOTES_KEK=$|SECURENOTES_KEK=${key}|" .env
    else
        printf 'SECURENOTES_KEK=%s\n' "$key" >> .env
    fi
    unset key
fi
