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

sharing_enabled="${SECURENOTES_SHARING_ENABLED:-true}"
if [[ "$sharing_enabled" != true ]]; then
    printf '%s\n' 'P7 smoke test requires SECURENOTES_SHARING_ENABLED=true.' >&2
    exit 1
fi

base_url="${SECURENOTES_BASE_URL:-http://localhost:5173}"
origin="${SECURENOTES_ORIGIN:-http://localhost:5173}"
run_id="$(openssl rand -hex 8)"
email_one="p7-smoke-${run_id}-a@example.test"
email_two="p7-smoke-${run_id}-b@example.test"
test_password='SecureNotes-P7-Smoke!2026'
cookie_one="$(mktemp)"
cookie_two="$(mktemp)"
cookie_anon="$(mktemp)"
response_file="$(mktemp)"
headers_file="$(mktemp)"
text_file="$(mktemp --suffix=.txt)"
download_file="$(mktemp)"
last_status=
note_id=
other_note_id=
view_share_id=
edit_share_id=
expired_share_id=
revoked_token=
sharing_override=0

cleanup() {
    if [[ "$sharing_override" == 1 ]]; then
        SECURENOTES_SHARING_ENABLED="$sharing_enabled" docker compose up -d --force-recreate api \
            >/dev/null 2>&1 || true
    fi

    local token
    if [[ -n "$note_id" ]]; then
        token="$(token_for "$cookie_one" 2>/dev/null || true)"
        if [[ -n "$token" ]]; then
            curl -sS -b "$cookie_one" -o /dev/null -X POST \
                -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
                "$base_url/api/notes/$note_id/delete" || true
        fi
    fi
    if [[ -n "$other_note_id" ]]; then
        token="$(token_for "$cookie_two" 2>/dev/null || true)"
        if [[ -n "$token" ]]; then
            curl -sS -b "$cookie_two" -o /dev/null -X POST \
                -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
                "$base_url/api/notes/$other_note_id/delete" || true
        fi
    fi

    local sql
    sql="BEGIN TRANSACTION;
UPDATE dbo.notes SET ParentId = NULL WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}'));
DELETE i FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId WHERE n.UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}'));
DELETE FROM dbo.notes WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}'));
DELETE FROM dbo.categories WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}'));
DELETE FROM dbo.users WHERE NormalizedEmail IN (N'${email_one}', N'${email_two}');
COMMIT TRANSACTION;"
    if ! docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -Q "$sql" \
        >/dev/null 2>&1
    then
        printf '%s\n' 'Warning: database cleanup for P7 smoke-test accounts failed.' >&2
    fi
    rm -f "$cookie_one" "$cookie_two" "$cookie_anon" "$response_file" "$headers_file" \
        "$text_file" "$download_file"
}
trap cleanup EXIT

json_field() {
    node -e '
      let input = "";
      process.stdin.on("data", chunk => input += chunk);
      process.stdin.on("end", () => {
        const value = JSON.parse(input)[process.argv[1]];
        if (value === undefined || value === null) process.exit(1);
        process.stdout.write(String(value));
      });
    ' "$1" < "$response_file"
}

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

request_post() {
    local jar="$1"
    local path="$2"
    local body="$3"
    local csrf="${4:-yes}"
    local token=""
    local args=(-sS -b "$jar" -c "$jar" -o "$response_file" -D "$headers_file" -w '%{http_code}' \
        -X POST -H "Origin: $origin" -H 'Content-Type: application/json')
    if [[ "$csrf" == yes ]]; then
        token="$(token_for "$jar")"
        args+=(-H "X-CSRF-TOKEN: $token")
    fi
    args+=(--data "$body")
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

wait_for_api() {
    for attempt in $(seq 1 30); do
        if curl -fsS "$base_url/api/health/ready" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    printf '%s\n' 'API did not become ready after changing the sharing feature flag.' >&2
    return 1
}

request_post "$cookie_one" /api/auth/register \
    "{\"email\":\"$email_one\",\"password\":\"$test_password\"}"
expect_status 201 'register share owner'
request_post "$cookie_two" /api/auth/register \
    "{\"email\":\"$email_two\",\"password\":\"$test_password\"}"
expect_status 201 'register separate signed-in editor'

request_post "$cookie_one" /api/notes \
    '{"title":"P7 original title","content":"Original shared text","color":"#dbeafe","importance":2,"isPinned":true,"position":0}'
expect_status 201 'create note to share'
note_id="$(json_field noteId)"

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":24}' no
expect_status 400 'require CSRF when creating share link'

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"admin","expiresInHours":24}'
expect_status 400 'reject unsupported share permission'
request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":721}'
expect_status 400 'reject share expiry beyond 30 days'

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":24}'
expect_status 201 'create view-only share'
view_share_id="$(json_field shareId)"
view_token="$(json_field token)"
if [[ ! "$view_token" =~ ^[A-Za-z0-9_-]{43}$ ]]; then
    printf '%s\n' 'Share token is not a 32-byte base64url token.' >&2
    exit 1
fi

stored_hash="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(64), TokenHash, 2) FROM dbo.share_links WHERE ShareId = '$view_share_id';" |
    tr -d '[:space:]')"
expected_hash="$(node -e 'process.stdout.write(require("node:crypto").createHash("sha256").update(process.argv[1], "base64url").digest("hex").toUpperCase())' "$view_token")"
if [[ "$stored_hash" != "$expected_hash" ]]; then
    printf '%s\n' 'Database does not contain the SHA-256 hash of the share token.' >&2
    exit 1
fi
printf '%s\n' 'PASS database stores only the token hash'

last_status="$(curl -sS -D "$headers_file" -o "$response_file" -w '%{http_code}' \
    "$base_url/api/shared/$view_token?permission=edit&id=1&id=2")"
expect_status 200 'read active shared note despite conflicting query parameters'
if [[ "$(json_field permission)" != view \
    || "$(json_field title)" != 'P7 original title' \
    || "$(grep -i '^Cache-Control:' "$headers_file" | tr -d '\r' | grep -c 'no-store')" != 1 ]]; then
    printf '%s\n' 'Share permission, content, or no-store response header was incorrect.' >&2
    exit 1
fi
if ! node -e 'const d=JSON.parse(require("fs").readFileSync(0,"utf8"));process.exit("userId" in d ? 1 : 0)' < "$response_file"; then
    printf '%s\n' 'Shared note response unexpectedly exposed the owner ID.' >&2
    exit 1
fi
printf '%s\n' 'PASS share permission comes from the database, not query parameters'

unknown_token="$(node -e 'process.stdout.write(require("node:crypto").randomBytes(32).toString("base64url"))')"
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' "$base_url/api/shared/$unknown_token")"
expect_status 404 'return uniform not-found for an unknown token'

printf '%s\n' 'P7 shared attachment text' > "$text_file"
csrf="$(token_for "$cookie_one")"
last_status="$(curl -sS -b "$cookie_one" -o "$response_file" -w '%{http_code}' \
    -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $csrf" \
    -F "file=@$text_file;type=text/plain" "$base_url/api/notes/$note_id/attachments")"
expect_status 201 'upload an attachment to the shared note'
shared_attachment_id="$(json_field attachmentId)"

request_post "$cookie_two" /api/notes \
    '{"title":"Not in shared note","content":"Other attachment owner","color":"#fff8c5","importance":0,"isPinned":false,"position":1}'
expect_status 201 'create a separate note with another owner'
other_note_id="$(json_field noteId)"
csrf="$(token_for "$cookie_two")"
last_status="$(curl -sS -b "$cookie_two" -o "$response_file" -w '%{http_code}' \
    -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $csrf" \
    -F "file=@$text_file;type=text/plain" "$base_url/api/notes/$other_note_id/attachments")"
expect_status 201 'upload unrelated attachment'
unrelated_attachment_id="$(json_field attachmentId)"

last_status="$(curl -sS -o "$response_file" -w '%{http_code}' \
    "$base_url/api/shared/$view_token/attachments")"
expect_status 200 'list attachments through a view link'
if ! grep -q "$shared_attachment_id" "$response_file" \
    || grep -q "$unrelated_attachment_id" "$response_file"; then
    printf '%s\n' 'Shared attachment listing was not scoped to the shared note.' >&2
    exit 1
fi
last_status="$(curl -sS -o "$download_file" -w '%{http_code}' \
    "$base_url/api/shared/$view_token/attachments/$shared_attachment_id/download")"
expect_status 200 'download an attachment through its share link'
cmp "$text_file" "$download_file"
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' \
    "$base_url/api/shared/$view_token/attachments/$unrelated_attachment_id/download")"
expect_status 404 'deny downloading an attachment from another note'

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"edit","expiresInHours":24}'
expect_status 201 'create an edit-only share'
edit_share_id="$(json_field shareId)"
edit_token="$(json_field token)"

csrf="$(token_for "$cookie_anon")"
last_status="$(curl -sS -b "$cookie_anon" -o "$response_file" -w '%{http_code}' \
    -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $csrf" \
    -H 'Content-Type: application/json' --data '{"title":"Unauthenticated edit","content":"No"}' \
    "$base_url/api/shared/$edit_token/update")"
expect_status 401 'require an application login for edit links'

request_post "$cookie_two" "/api/shared/$edit_token/update" \
    '{"title":"Edited over share","content":"Updated by authenticated collaborator"}'
expect_status 200 'allow signed-in collaborator to edit title and content'
if [[ "$(json_field title)" != 'Edited over share' \
    || "$(json_field content)" != 'Updated by authenticated collaborator' ]]; then
    printf '%s\n' 'Shared edit response did not contain the requested text.' >&2
    exit 1
fi

last_status="$(curl -sS -b "$cookie_one" -o "$response_file" -w '%{http_code}' \
    "$base_url/api/notes/$note_id")"
expect_status 200 'owner can still read the edited note'
if [[ "$(json_field title)" != 'Edited over share' \
    || "$(json_field color)" != '#dbeafe' \
    || "$(json_field importance)" != 2 \
    || "$(json_field isPinned)" != true ]]; then
    printf '%s\n' 'Shared edit changed fields outside title/content or failed to persist.' >&2
    exit 1
fi

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":24}'
expect_status 201 'create share to revoke'
revoke_id="$(json_field shareId)"
revoked_token="$(json_field token)"
request_post "$cookie_one" "/api/notes/$note_id/shares/$revoke_id/revoke" '{}'
expect_status 204 'revoke share link'
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' "$base_url/api/shared/$revoked_token")"
expect_status 404 'return not-found for a revoked token'

request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":24}'
expect_status 201 'create share to expire'
expired_share_id="$(json_field shareId)"
expired_token="$(json_field token)"
docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b \
    -Q "UPDATE dbo.share_links SET CreatedAt = DATEADD(minute, -2, SYSUTCDATETIME()), ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE ShareId = '$expired_share_id';" \
    >/dev/null
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' "$base_url/api/shared/$expired_token")"
expect_status 404 'return not-found for an expired token'

request_post "$cookie_two" "/api/notes/$note_id/shares" \
    '{"permission":"edit","expiresInHours":24}'
expect_status 404 'deny another account from managing the note share'

audit_count="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.audit_logs WHERE Action IN ('share_create','share_revoke','share_access') AND ResourceType = 'note_share';" |
    tr -d '[:space:]')"
if [[ "$audit_count" -lt 6 ]]; then
    printf 'Expected share create/revoke/access audit events, found %s.\n' "$audit_count" >&2
    exit 1
fi
printf '%s\n' 'PASS share creation, revocation, and access are audited'

rate_limited=0
for attempt in $(seq 1 70); do
    status="$(curl -sS -o /dev/null -w '%{http_code}' "$base_url/api/shared/$view_token")"
    if [[ "$status" == 429 ]]; then
        rate_limited=1
        break
    fi
done
if [[ "$rate_limited" != 1 ]]; then
    printf '%s\n' 'Shared-link requests were not rate limited by client IP.' >&2
    exit 1
fi
printf '%s\n' 'PASS public shared-link reads are rate limited by client IP'

SECURENOTES_SHARING_ENABLED=false docker compose up -d --force-recreate api
sharing_override=1
wait_for_api
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' "$base_url/api/shared/$view_token")"
expect_status 503 'disable public share access with the feature kill switch'
request_post "$cookie_one" "/api/notes/$note_id/shares" \
    '{"permission":"view","expiresInHours":24}'
expect_status 503 'disable share creation with the feature kill switch'

SECURENOTES_SHARING_ENABLED="$sharing_enabled" docker compose up -d --force-recreate api
sharing_override=0
wait_for_api
printf '%s\n' 'PASS sharing kill switch restored from local configuration'

printf '%s\n' 'P7 share-link smoke tests passed.'
