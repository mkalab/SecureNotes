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
email_one="p2-smoke-${run_id}-a@example.test"
email_two="p2-smoke-${run_id}-b@example.test"
email_three="p2-smoke-${run_id}-c@example.test"
missing_email="missing-${run_id}@example.test"
test_password='SecureNotes-P2-Smoke!2026'
cookie_one="$(mktemp)"
cookie_two="$(mktemp)"
cookie_three="$(mktemp)"
cookie_old_session="$(mktemp)"
cookie_logout_old="$(mktemp)"
response_file="$(mktemp)"
response_headers="$(mktemp)"
large_body_file="$(mktemp)"
last_status=
note_id=
child_id=
category_id=

cleanup() {
    local sql
    sql="BEGIN TRANSACTION;
UPDATE dbo.notes SET ParentId = NULL WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}', N'${email_three}'));
DELETE i FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId WHERE n.UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}', N'${email_three}'));
DELETE FROM dbo.notes WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}', N'${email_three}'));
DELETE FROM dbo.categories WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}', N'${email_three}'));
DELETE FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}', N'${email_three}');
COMMIT TRANSACTION;"

    if ! docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -Q "$sql" \
        >/dev/null 2>&1
    then
        printf '%s\n' 'Warning: database cleanup for P2 smoke-test accounts failed.' >&2
    fi

    rm -f "$cookie_one" "$cookie_two" "$cookie_three" "$cookie_old_session" "$cookie_logout_old" \
        "$response_file" "$response_headers" "$large_body_file"
}
trap cleanup EXIT

json_field() {
    local field="$1"
    node -e '
      let input = "";
      process.stdin.on("data", chunk => input += chunk);
      process.stdin.on("end", () => {
        const value = JSON.parse(input)[process.argv[1]];
        if (value === undefined || value === null) process.exit(1);
        process.stdout.write(String(value));
      });
    ' "$field" < "$response_file"
}

token_for() {
    local jar="$1"
    curl -fsS -b "$jar" -c "$jar" "$base_url/api/csrf" |
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

request() {
    local method="$1"
    local jar="$2"
    local path="$3"
    local body="${4:-}"
    local csrf="${5:-yes}"
    local request_origin="${6:-$origin}"
    local token=""
    local args=(-sS -b "$jar" -c "$jar" -o "$response_file" -D "$response_headers" -w '%{http_code}' -X "$method")

    if [[ "$method" == POST ]]; then
        args+=(-H 'Content-Type: application/json')
        if [[ "$request_origin" != none ]]; then
            args+=(-H "Origin: $request_origin")
        fi
        if [[ "$csrf" == yes ]]; then
            token="$(token_for "$jar")"
            args+=(-H "X-CSRF-TOKEN: $token")
        fi
        if [[ -n "$body" ]]; then
            if [[ "$body" == @* ]]; then
                args+=(--data-binary "$body")
            else
                args+=(--data "$body")
            fi
        fi
    fi

    last_status="$(curl "${args[@]}" "$base_url$path")"
}

expect_status() {
    local expected="$1"
    local description="$2"
    if [[ "$last_status" != "$expected" ]]; then
        printf '%s: expected HTTP %s, got HTTP %s\n' "$description" "$expected" "$last_status" >&2
        if [[ -s "$response_file" ]]; then
            cat "$response_file" >&2
            printf '\n' >&2
        fi
        exit 1
    fi
    printf 'PASS %s (HTTP %s)\n' "$description" "$last_status"
}

curl -fsS "$base_url/api/health" >/dev/null
curl -fsS "$base_url/api/health/ready" >/dev/null
curl -sS -D "$response_headers" -o /dev/null "$base_url/api/health"
for header in \
    'Content-Security-Policy' \
    'X-Content-Type-Options' \
    'Referrer-Policy' \
    'Permissions-Policy' \
    'Cache-Control'
do
    if ! grep -qi "^${header}:" "$response_headers"; then
        printf 'Missing security response header: %s\n' "$header" >&2
        exit 1
    fi
done
grep -qi '^X-Content-Type-Options: nosniff' "$response_headers"
grep -qi '^Referrer-Policy: no-referrer' "$response_headers"
grep -qi '^Cache-Control: no-store' "$response_headers"
grep -qi "object-src 'none'" "$response_headers"
grep -qi "frame-ancestors 'none'" "$response_headers"
if grep -qi '^Server:' "$response_headers"; then
    printf '%s\n' 'API response unexpectedly discloses a Server header.' >&2
    exit 1
fi
printf '%s\n' 'PASS API security headers'

curl -fsS -D "$response_headers" -o /dev/null "$base_url/"
grep -qi '^Content-Security-Policy:' "$response_headers"
printf '%s\n' 'PASS frontend security headers'

google_status="$(curl -sS -o /dev/null -D "$response_headers" -w '%{http_code}' \
    --get --data-urlencode 'returnUrl=https://attacker.example' "$base_url/api/auth/google")"
if [[ "$google_status" != 302 ]]; then
    printf 'Google OIDC start expected HTTP 302, got HTTP %s\n' "$google_status" >&2
    exit 1
fi
google_location="$(awk 'tolower($1) == "location:" {$1=""; sub(/^[[:space:]]+/, ""); sub(/\r$/, ""); print; exit}' "$response_headers")"
if [[ "$google_location" == *'attacker.example'* ]] \
    || [[ "$google_location" != /* && "$google_location" != https://accounts.google.com/* ]]; then
    printf 'Google OIDC returned an unsafe redirect: %s\n' "$google_location" >&2
    exit 1
fi
printf '%s\n' 'PASS Google OIDC start rejects external return URLs'

callback_status="$(curl -sS -o /dev/null -D "$response_headers" -w '%{http_code}' \
    "$base_url/signin-oidc")"
if [[ -n "${GOOGLE_OIDC_CLIENT_ID:-}" && -n "${GOOGLE_OIDC_CLIENT_SECRET:-}" \
    && -n "${GOOGLE_OIDC_REDIRECT_URI:-}" ]]; then
    if [[ "$callback_status" != 302 ]] \
        || ! grep -qi '^Location: .*authError=google_failed' "$response_headers"; then
        printf 'OIDC callback without valid state was not rejected safely (HTTP %s).\n' "$callback_status" >&2
        exit 1
    fi
    printf '%s\n' 'PASS Google OIDC callback without state is rejected'
else
    if [[ "$callback_status" != 404 ]]; then
        printf 'Unconfigured OIDC callback expected HTTP 404, got HTTP %s.\n' "$callback_status" >&2
        exit 1
    fi
    printf '%s\n' 'SKIP Google OIDC callback validation (provider not configured)'
fi

request POST "$cookie_one" /api/auth/register \
    "{\"email\":\"$email_one\",\"password\":\"$test_password\"}"
expect_status 201 'register first user'
user_one_id="$(json_field userId)"
if [[ ! "$user_one_id" =~ ^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$ ]]; then
    printf '%s\n' 'Public account ID is not a GUID.' >&2
    exit 1
fi
session_cookie_header="$(grep -i '^Set-Cookie: SecureNotes.Dev=' "$response_headers" || true)"
if [[ -z "$session_cookie_header" ]]; then
    printf '%s\n' 'Login response did not set the expected development session cookie.' >&2
    exit 1
fi
grep -qi 'httponly' <<<"$session_cookie_header"
grep -qi 'samesite=strict' <<<"$session_cookie_header"
if grep -qi 'domain=' <<<"$session_cookie_header"; then
    printf '%s\n' 'Session cookie unexpectedly sets a Domain attribute.' >&2
    exit 1
fi
printf '%s\n' 'PASS session cookie flags'

link_without_csrf="$(curl -sS -b "$cookie_one" -c "$cookie_one" -o "$response_file" \
    -D "$response_headers" -w '%{http_code}' -X POST \
    -H "Origin: $origin" -H 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "Password=$test_password" "$base_url/api/auth/google/link")"
if [[ "$link_without_csrf" != 400 ]]; then
    printf 'Google account linking without CSRF expected HTTP 400, got HTTP %s\n' "$link_without_csrf" >&2
    exit 1
fi
printf '%s\n' 'PASS Google account linking requires CSRF'

request POST "$cookie_one" /api/auth/register \
    "{\"email\":\"invalid-${run_id}@example.test\",\"password\":\"short\"}"
expect_status 400 'reject weak registration password'

request POST "$cookie_one" /api/categories '{"name":"cross-origin"}' yes 'https://attacker.example'
expect_status 403 'reject cross-origin state change'
request POST "$cookie_one" /api/categories '{"name":"missing-origin"}' yes none
expect_status 403 'reject state change without Origin'

request POST "$cookie_one" /api/auth/login \
    "{\"email\":\"$email_one\",\"password\":\"$test_password\"}"
expect_status 200 'login first user'
cp "$cookie_one" "$cookie_old_session"
request POST "$cookie_one" /api/auth/login \
    "{\"email\":\"$email_one\",\"password\":\"$test_password\"}"
expect_status 200 'issue a fresh session on login'
request GET "$cookie_old_session" /api/categories
expect_status 401 'revoke the previous session on login'
request POST "$cookie_one" /api/notes '{}' no
expect_status 400 'reject POST without CSRF token'

request POST "$cookie_one" /api/categories '{"name":"P2 smoke category"}'
expect_status 201 'create category'
category_id="$(json_field categoryId)"
if [[ ! "$category_id" =~ ^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$ ]]; then
    printf '%s\n' 'Public category ID is not a GUID.' >&2
    exit 1
fi

request GET "$cookie_one" /api/categories
expect_status 200 'list owned categories'
request POST "$cookie_one" "/api/categories/$category_id/update" '{"name":"P2 smoke renamed"}'
expect_status 200 'update category'

request POST "$cookie_one" /api/categories '{"name":"P2 smoke renamed"}'
expect_status 409 'reject duplicate category'

spoof_user_id="$(node -e 'process.stdout.write(require("node:crypto").randomUUID())')"
note_body="{\"userId\":\"$spoof_user_id\",\"title\":\"P2 smoke parent $run_id\",\"content\":\"searchable smoke content\",\"categoryId\":\"$category_id\",\"parentId\":null,\"sourceUrl\":\"https://example.test/source\",\"color\":\"#fff8c5\",\"importance\":2,\"isPinned\":true,\"position\":0}"
request POST "$cookie_one" /api/notes "$note_body"
expect_status 201 'create note'
note_id="$(json_field noteId)"
if [[ ! "$note_id" =~ ^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$ ]]; then
    printf '%s\n' 'Public note ID is not a GUID.' >&2
    exit 1
fi
if [[ "$(json_field userId)" != "$user_one_id" ]]; then
    printf '%s\n' 'The API trusted a request-supplied userId instead of the authenticated principal.' >&2
    exit 1
fi
printf '%s\n' 'PASS derive resource ownership from the authenticated principal'

encrypted_note_state="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W -s '|' \
    -Q "SET NOCOUNT ON;
        SELECT CASE WHEN n.Title = N'' AND n.Content = N'' THEN 1 ELSE 0 END,
               CASE WHEN DATALENGTH(n.TitleEncrypted) > 28
                         AND DATALENGTH(n.ContentEncrypted) > 28
                         AND SUBSTRING(n.TitleEncrypted, 1, 12) <> SUBSTRING(n.ContentEncrypted, 1, 12)
                    THEN 1 ELSE 0 END,
               CASE WHEN DATALENGTH(u.DekCiphertext) >= 60 THEN 1 ELSE 0 END
        FROM dbo.notes n
        JOIN dbo.users u ON u.UserId = n.UserId
        WHERE n.NoteId = N'$note_id' AND u.NormalizedEmail = N'$email_one';" | tr -d '[:space:]')"
if [[ "$encrypted_note_state" != '1|1|1' ]]; then
    printf 'Note encryption-at-rest check failed: %s\n' "$encrypted_note_state" >&2
    exit 1
fi
printf '%s\n' 'PASS note fields are authenticated ciphertext and the DEK is wrapped'

request GET "$cookie_one" "/api/notes/$note_id"
expect_status 200 'get owned note'

request POST "$cookie_one" /api/notes \
    "{\"title\":\"unsafe source $run_id\",\"content\":\"test\",\"categoryId\":null,\"parentId\":null,\"sourceUrl\":\"javascript:alert(1)\",\"color\":\"#ffffff\",\"importance\":0,\"isPinned\":false,\"position\":0}"
expect_status 400 'reject non-HTTP source URL'

request POST "$cookie_one" /api/notes \
    "{\"title\":\"P2 smoke child $run_id\",\"content\":\"nested note\",\"categoryId\":null,\"parentId\":\"$note_id\",\"sourceUrl\":null,\"color\":\"#ffffff\",\"importance\":0,\"isPinned\":false,\"position\":1}"
expect_status 201 'create child note'
child_id="$(json_field noteId)"

request POST "$cookie_one" /api/notes \
    "{\"title\":\"Invalid nested child $run_id\",\"content\":\"not supported\",\"categoryId\":null,\"parentId\":\"$child_id\",\"sourceUrl\":null,\"color\":\"#ffffff\",\"importance\":0,\"isPinned\":false,\"position\":2}"
expect_status 404 'reject a child note nested beyond one level'

request GET "$cookie_one" "/api/notes?search=$run_id"
expect_status 200 'search owned notes'
node -e '
  let input = "";
  process.stdin.on("data", chunk => input += chunk);
  process.stdin.on("end", () => {
    const rows = JSON.parse(input);
    if (rows.length !== 2) process.exit(1);
  });
' < "$response_file" || {
    printf '%s\n' 'Search did not return both matching owned notes.' >&2
    exit 1
}

request POST "$cookie_one" "/api/notes/$note_id/items" \
    '{"title":"Smoke checklist item","isCompleted":false}'
expect_status 201 'create checklist item'
item_id="$(json_field noteItemId)"
encrypted_item_state="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W -s '|' \
    -Q "SET NOCOUNT ON;
        SELECT CASE WHEN i.Title = N'' AND DATALENGTH(i.TitleEncrypted) > 28 THEN 1 ELSE 0 END
        FROM dbo.note_items i
        WHERE i.NoteItemId = N'$item_id';" | tr -d '[:space:]')"
if [[ "$encrypted_item_state" != '1' ]]; then
    printf 'Checklist title encryption-at-rest check failed: %s\n' "$encrypted_item_state" >&2
    exit 1
fi
printf '%s\n' 'PASS checklist title is encrypted at rest'

request GET "$cookie_one" "/api/notes/$note_id/items"
expect_status 200 'list checklist items'

request POST "$cookie_one" "/api/notes/$note_id/items/$item_id/update" \
    '{"title":"Updated checklist item","isCompleted":true}'
expect_status 200 'update checklist item'
if [[ "$(json_field title)" != 'Updated checklist item' || "$(json_field isCompleted)" != true ]]; then
    printf '%s\n' 'Checklist update response did not contain the expected values.' >&2
    exit 1
fi

request POST "$cookie_one" "/api/notes/$note_id/update" \
    "{\"title\":\"Updated P2 smoke parent $run_id\",\"content\":\"updated\",\"categoryId\":\"$category_id\",\"parentId\":null,\"sourceUrl\":null,\"color\":\"#dbeafe\",\"importance\":4,\"isPinned\":false,\"position\":3}"
expect_status 200 'update note'

request POST "$cookie_two" /api/auth/register \
    "{\"email\":\"$email_two\",\"password\":\"$test_password\"}"
expect_status 201 'register second user'

login_start_ms="$(date +%s%3N)"
request POST "$cookie_three" /api/auth/login \
    "{\"email\":\"$missing_email\",\"password\":\"$test_password\"}"
expect_status 401 'reject an unknown account'
login_elapsed_ms="$(( $(date +%s%3N) - login_start_ms ))"
if (( login_elapsed_ms < 500 )); then
    printf 'Unknown-account failure returned too quickly (%s ms).\n' "$login_elapsed_ms" >&2
    exit 1
fi
generic_login_error="$(cat "$response_file")"

request POST "$cookie_three" /api/auth/register \
    "{\"email\":\"$email_three\",\"password\":\"$test_password\"}"
expect_status 201 'register third user for lockout check'
login_start_ms="$(date +%s%3N)"
request POST "$cookie_three" /api/auth/login \
    "{\"email\":\"$email_three\",\"password\":\"wrong-password\"}"
expect_status 401 'reject an incorrect password'
login_elapsed_ms="$(( $(date +%s%3N) - login_start_ms ))"
if (( login_elapsed_ms < 500 )); then
    printf 'Known-account failure returned too quickly (%s ms).\n' "$login_elapsed_ms" >&2
    exit 1
fi
if [[ "$(cat "$response_file")" != "$generic_login_error" ]]; then
    printf '%s\n' 'Login responses differ for known and unknown accounts.' >&2
    exit 1
fi
for attempt in 2 3 4 5; do
    request POST "$cookie_three" /api/auth/login \
        "{\"email\":\"$email_three\",\"password\":\"wrong-password\"}"
    expect_status 401 "reject incorrect password attempt $attempt"
done
request POST "$cookie_three" /api/auth/login \
    "{\"email\":\"$email_three\",\"password\":\"$test_password\"}"
expect_status 401 'temporarily lock an account after repeated failures'
if [[ "$(cat "$response_file")" != "$generic_login_error" ]]; then
    printf '%s\n' 'Login responses differ for invalid credentials and locked accounts.' >&2
    exit 1
fi
printf '%s\n' 'PASS generic authentication errors and temporary lockout'

request GET "$cookie_two" "/api/notes/$note_id"
expect_status 404 'hide another user note'
request GET "$cookie_two" "/api/notes?search=$run_id"
expect_status 200 'list only the second user notes'
node -e '
  let input = "";
  process.stdin.on("data", chunk => input += chunk);
  process.stdin.on("end", () => {
    if (JSON.parse(input).length !== 0) process.exit(1);
  });
' < "$response_file" || {
    printf '%s\n' 'A user saw notes belonging to another account.' >&2
    exit 1
}

request POST "$cookie_two" "/api/notes/$note_id/items" '{"title":"unauthorized","isCompleted":false}'
expect_status 404 'hide another user checklist'
request POST "$cookie_two" "/api/notes/$note_id/items/$item_id/update" '{"title":"unauthorized","isCompleted":false}'
expect_status 404 'hide another user checklist item'
request POST "$cookie_two" "/api/categories/$category_id/update" '{"name":"unauthorized"}'
expect_status 404 'hide another user category'

request POST "$cookie_one" "/api/notes/$note_id/items/$item_id/delete" '{}'
expect_status 204 'delete checklist item'
request POST "$cookie_one" "/api/notes/$child_id/delete" '{}'
expect_status 204 'delete child note'
request POST "$cookie_one" "/api/notes/$note_id/delete" '{}'
expect_status 204 'delete note'
request POST "$cookie_one" "/api/categories/$category_id/delete" '{}'
expect_status 204 'delete category'

node -e 'process.stdout.write("x".repeat(1048577))' > "$large_body_file"
request POST "$cookie_one" /api/auth/login "@$large_body_file"
expect_status 413 'reject an oversized request body'

cp "$cookie_one" "$cookie_logout_old"
request POST "$cookie_one" /api/auth/logout '{}'
expect_status 204 'logout first user'
request GET "$cookie_logout_old" /api/categories
expect_status 401 'revoke the server-side session on logout'
request POST "$cookie_two" "/api/notes/$note_id/delete" '{}'
expect_status 404 'record a repeated ownership violation'
request GET "$cookie_two" /api/categories
expect_status 401 'revoke session after repeated ownership violations'

printf '%s\n' 'P2 API smoke tests passed.'
