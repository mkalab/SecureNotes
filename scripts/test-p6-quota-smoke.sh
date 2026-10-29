#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

for command in curl node openssl docker; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'Required command not found: %s\n' "$command" >&2
        exit 1
    fi
done

if [[ ! -f .env ]]; then
    printf '%s\n' 'Missing .env; run ./scripts/init-dev-env.sh first.' >&2
    exit 1
fi

set -a
. ./.env
set +a
: "${NOTES_MIGRATOR_PASSWORD:?NOTES_MIGRATOR_PASSWORD is missing from .env}"

base_url="${SECURENOTES_BASE_URL:-http://localhost:5173}"
origin="${SECURENOTES_ORIGIN:-http://localhost:5173}"
run_id="$(openssl rand -hex 8)"
email="p6-quota-${run_id}@example.test"
test_password='SecureNotes-P6-Quota!2026'
cookie="$(mktemp)"
response_file="$(mktemp)"
file_one="$(mktemp --suffix=.txt)"
file_two="$(mktemp --suffix=.txt)"
status_one="$(mktemp)"
status_two="$(mktemp)"
body_one="$(mktemp)"
body_two="$(mktemp)"
note_id=

cleanup() {
    if [[ -n "$note_id" ]]; then
        local token
        token="$(token_for "$cookie" 2>/dev/null || true)"
        if [[ -n "$token" ]]; then
            curl -sS -b "$cookie" -o /dev/null -X POST \
                -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
                "$base_url/api/notes/$note_id/delete" || true
        fi
    fi

    local sql
    sql="BEGIN TRANSACTION;
UPDATE dbo.notes SET ParentId = NULL WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail = N'${email}');
DELETE i FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId WHERE n.UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail = N'${email}');
DELETE FROM dbo.notes WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail = N'${email}');
DELETE FROM dbo.categories WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail = N'${email}');
DELETE FROM dbo.users WHERE NormalizedEmail = N'${email}';
COMMIT TRANSACTION;"
    docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -Q "$sql" \
        >/dev/null 2>&1 || true

    docker compose up -d --force-recreate api >/dev/null 2>&1 || true
    rm -f "$cookie" "$response_file" "$file_one" "$file_two" \
        "$status_one" "$status_two" "$body_one" "$body_two"
}
trap cleanup EXIT

token_for() {
    curl -fsS -b "$1" -c "$1" "$base_url/api/csrf" |
        node -e '
          let input = "";
          process.stdin.on("data", chunk => input += chunk);
          process.stdin.on("end", () => {
            const value = JSON.parse(input).requestToken;
            if (!value) process.exit(1);
            process.stdout.write(value);
          });
        '
}

wait_for_api() {
    for attempt in $(seq 1 30); do
        if curl -fsS "$base_url/api/health/ready" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    printf '%s\n' 'API did not become ready after the quota configuration change.' >&2
    return 1
}

SECURENOTES_ATTACHMENTS_MAX_FILES_PER_NOTE=1 docker compose up -d --force-recreate api
wait_for_api

token="$(token_for "$cookie")"
status="$(curl -sS -b "$cookie" -c "$cookie" -o "$response_file" -w '%{http_code}' \
    -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
    -H 'Content-Type: application/json' \
    --data "{\"email\":\"$email\",\"password\":\"$test_password\"}" \
    "$base_url/api/auth/register")"
if [[ "$status" != 201 ]]; then
    printf 'Test account registration failed (HTTP %s).\n' "$status" >&2
    exit 1
fi

token="$(token_for "$cookie")"
status="$(curl -sS -b "$cookie" -c "$cookie" -o "$response_file" -w '%{http_code}' \
    -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
    -H 'Content-Type: application/json' \
    --data '{"title":"P6 concurrent quota test","content":"race","color":"#fff8c5","importance":1,"isPinned":false,"position":0}' \
    "$base_url/api/notes")"
if [[ "$status" != 201 ]]; then
    printf 'Test note creation failed (HTTP %s).\n' "$status" >&2
    exit 1
fi
note_id="$(node -e '
  let input = "";
  process.stdin.on("data", chunk => input += chunk);
  process.stdin.on("end", () => process.stdout.write(JSON.parse(input).noteId));
' < "$response_file")"

printf '%s\n' 'Concurrent upload one' > "$file_one"
printf '%s\n' 'Concurrent upload two' > "$file_two"
token="$(token_for "$cookie")"
curl -sS -b "$cookie" -o "$body_one" -w '%{http_code}' -X POST \
    -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
    -F "file=@$file_one;type=text/plain" \
    "$base_url/api/notes/$note_id/attachments" > "$status_one" &
pid_one=$!
curl -sS -b "$cookie" -o "$body_two" -w '%{http_code}' -X POST \
    -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
    -F "file=@$file_two;type=text/plain" \
    "$base_url/api/notes/$note_id/attachments" > "$status_two" &
pid_two=$!
wait "$pid_one"
wait "$pid_two"

result_one="$(<"$status_one")"
result_two="$(<"$status_two")"
if ! { [[ "$result_one" == 201 && "$result_two" == 409 ]] \
    || [[ "$result_one" == 409 && "$result_two" == 201 ]]; }; then
    printf 'Expected one HTTP 201 and one HTTP 409, received %s and %s.\n' \
        "$result_one" "$result_two" >&2
    exit 1
fi

count="$(curl -fsS -b "$cookie" "$base_url/api/notes/$note_id/attachments" |
    node -e '
      let input = "";
      process.stdin.on("data", chunk => input += chunk);
      process.stdin.on("end", () => process.stdout.write(String(JSON.parse(input).length)));
    ')"
if [[ "$count" != 1 ]]; then
    printf 'Expected exactly one stored attachment after the concurrent uploads, got %s.\n' "$count" >&2
    exit 1
fi

printf 'PASS concurrent uploads respect the one-file quota (HTTP %s and %s).\n' \
    "$result_one" "$result_two"
