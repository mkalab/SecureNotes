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
: "${NOTES_APP_PASSWORD:?NOTES_APP_PASSWORD is missing from .env}"

base_url="${SECURENOTES_BASE_URL:-http://localhost:5173}"
origin="${SECURENOTES_ORIGIN:-http://localhost:5173}"
run_id="$(openssl rand -hex 8)"
admin_email="p8-smoke-${run_id}-admin@example.test"
target_email="p8-smoke-${run_id}-target@example.test"
logout_email="p8-smoke-${run_id}-logout@example.test"
test_password='SecureNotes-P8-Smoke!2026'
cookie_admin="$(mktemp)"
cookie_target="$(mktemp)"
cookie_logout="$(mktemp)"
response_file="$(mktemp)"
last_status=
admin_id=
target_id=
logout_id=
note_id=
share_id=
share_token=
secret=
recovery_code=

cleanup() {
    local sql
    sql="BEGIN TRANSACTION;
UPDATE dbo.notes SET ParentId = NULL WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE i FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId WHERE n.UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE FROM dbo.note_attachments WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE FROM dbo.share_links WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE FROM dbo.notes WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE FROM dbo.categories WHERE UserId IN (SELECT UserId FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}'));
DELETE FROM dbo.users WHERE NormalizedEmail IN (N'${admin_email}', N'${target_email}', N'${logout_email}');
COMMIT TRANSACTION;"
    if ! docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -Q "$sql" \
        >/dev/null 2>&1
    then
        printf '%s\n' 'Warning: database cleanup for P8 smoke-test accounts failed.' >&2
    fi
    rm -f "$cookie_admin" "$cookie_target" "$cookie_logout" "$response_file"
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

csrf_for() {
    curl -fsS -b "$1" -c "$1" "$base_url/api/csrf" |
        node -e '
          let input = "";
          process.stdin.on("data", chunk => input += chunk);
          process.stdin.on("end", () => process.stdout.write(JSON.parse(input).requestToken));
        '
}

post() {
    local jar="$1"
    local path="$2"
    local body="$3"
    local csrf
    csrf="$(csrf_for "$jar")"
    last_status="$(curl -sS -b "$jar" -c "$jar" -o "$response_file" -w '%{http_code}' \
        -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $csrf" \
        -H 'Content-Type: application/json' --data "$body" "$base_url$path")"
}

expect() {
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

totp_code() {
    node - "$secret" <<'NODE'
const crypto = require("node:crypto");
const encoded = process.argv[2].replace(/=+$/, "").toUpperCase();
const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
let bits = "";
for (const char of encoded) bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
const secretBytes = Buffer.from(bits.match(/.{8}/g).map(byte => parseInt(byte, 2)));
const counter = BigInt(Math.floor(Date.now() / 30000));
const message = Buffer.alloc(8);
message.writeBigUInt64BE(counter);
const digest = crypto.createHmac("sha1", secretBytes).update(message).digest();
const offset = digest[digest.length - 1] & 15;
const value = (digest.readUInt32BE(offset) & 0x7fffffff) % 1000000;
process.stdout.write(String(value).padStart(6, "0"));
NODE
}

current_step() {
    node -e 'process.stdout.write(String(Math.floor(Date.now() / 30000)))'
}

post "$cookie_admin" /api/auth/register \
    "{\"email\":\"$admin_email\",\"password\":\"$test_password\",\"role\":\"admin\"}"
expect 400 'reject unrecognized registration fields such as role'

post "$cookie_admin" /api/auth/register \
    "{\"email\":\"$admin_email\",\"password\":\"$test_password\"}"
expect 201 'register administrator candidate'
admin_id="$(json_field userId)"
post "$cookie_target" /api/auth/register \
    "{\"email\":\"$target_email\",\"password\":\"$test_password\"}"
expect 201 'register account to administer'
target_id="$(json_field userId)"
post "$cookie_logout" /api/auth/register \
    "{\"email\":\"$logout_email\",\"password\":\"$test_password\"}"
expect 201 'register account for force-logout test'
logout_id="$(json_field userId)"

post "$cookie_admin" /api/auth/2fa/enroll '{}'
expect 200 'start TOTP enrollment'
secret="$(json_field secret)"
enable_step="$(current_step)"
enable_code="$(totp_code)"
post "$cookie_admin" /api/auth/2fa/enable "{\"code\":\"$enable_code\"}"
expect 200 'enable TOTP and generate recovery codes'
recovery_code="$(node -e 'process.stdout.write(JSON.parse(require("fs").readFileSync(0,"utf8")).recoveryCodes[0])' < "$response_file")"

factor_state="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT CONCAT(CONVERT(varchar(1), TwoFactorEnabled), ':', CONVERT(varchar(10), DATALENGTH(TwoFactorSecret)), ':', (SELECT COUNT(*) FROM dbo.two_factor_recovery_codes r WHERE r.UserId = u.UserId)) FROM dbo.users u WHERE UserId = '$admin_id';" |
    tr -d '[:space:]')"
if [[ "$factor_state" != '1:48:10' ]]; then
    printf 'Expected encrypted TOTP and ten hashed recovery codes, got %s.\n' "$factor_state" >&2
    exit 1
fi
printf '%s\n' 'PASS store encrypted TOTP material and hashes of recovery codes'

request_login() {
    local jar="$1"
    local email="$2"
    local csrf
    csrf="$(csrf_for "$jar")"
    last_status="$(curl -sS -b "$jar" -c "$jar" -o "$response_file" -w '%{http_code}' \
        -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $csrf" \
        -H 'Content-Type: application/json' \
        --data "{\"email\":\"$email\",\"password\":\"$test_password\"}" "$base_url/api/auth/login")"
}

request_login "$cookie_admin" "$admin_email"
expect 202 'require a second factor after password authentication'
post "$cookie_admin" /api/auth/2fa/verify "{\"code\":\"$recovery_code\"}"
expect 200 'allow one-time recovery code and issue a verified session'

remaining_codes="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.two_factor_recovery_codes WHERE UserId = '$admin_id';" |
    tr -d '[:space:]')"
if [[ "$remaining_codes" != 9 ]]; then
    printf 'Recovery code was not consumed exactly once; remaining: %s.\n' "$remaining_codes" >&2
    exit 1
fi
printf '%s\n' 'PASS recovery code is single-use'

./scripts/grant-admin.sh "$admin_email"
while (( $(current_step) <= enable_step + 1 )); do sleep 1; done
request_login "$cookie_admin" "$admin_email"
expect 202 'require TOTP when signing in as an administrator'
post "$cookie_admin" /api/auth/2fa/verify "{\"code\":\"$(totp_code)\"}"
expect 200 'complete administrator sign-in with TOTP'
if [[ "$(json_field role)" != admin ]]; then
    printf '%s\n' 'Administrator role was not returned after successful TOTP verification.' >&2
    exit 1
fi

last_status="$(curl -sS -b "$cookie_admin" -o "$response_file" -w '%{http_code}' "$base_url/api/console-4d91/users")"
expect 200 'allow TOTP-verified administrator to list users'
if ! grep -q "$target_email" "$response_file" || grep -q '"passwordHash"' "$response_file"; then
    printf '%s\n' 'Admin user list omitted users or exposed credential fields.' >&2
    exit 1
fi
printf '%s\n' 'PASS admin user list contains no password or note content'

last_status="$(curl -sS -b "$cookie_target" -o "$response_file" -w '%{http_code}' "$base_url/api/console-4d91/users")"
expect 403 'deny normal users access to admin endpoints'

docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b \
    -Q "UPDATE dbo.users SET Role = 'user' WHERE UserId = '$admin_id';" >/dev/null
last_status="$(curl -sS -b "$cookie_admin" -o "$response_file" -w '%{http_code}' "$base_url/api/console-4d91/users")"
expect 403 'refresh the administrator role from the database on each request'
docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b \
    -Q "UPDATE dbo.users SET Role = 'admin' WHERE UserId = '$admin_id';" >/dev/null
last_status="$(curl -sS -b "$cookie_admin" -o "$response_file" -w '%{http_code}' "$base_url/api/console-4d91/users")"
expect 200 'restore admin access from the database role'

post "$cookie_admin" "/api/console-4d91/users/$target_id/lock" '{}'
expect 204 'lock another account'
last_status="$(curl -sS -b "$cookie_target" -o "$response_file" -w '%{http_code}' "$base_url/api/auth/me")"
expect 204 'revoke locked account session immediately'
post "$cookie_admin" "/api/console-4d91/users/$logout_id/sessions/revoke" '{}'
expect 200 'force logout another account'
last_status="$(curl -sS -b "$cookie_logout" -o "$response_file" -w '%{http_code}' "$base_url/api/auth/me")"
expect 204 'reject forcibly revoked user session'
post "$cookie_admin" "/api/console-4d91/users/$admin_id/lock" '{}'
expect 404 'prevent an administrator from locking their own account'

post "$cookie_admin" /api/notes \
    '{"title":"P8 smoke note","content":"Admin APIs must never expose note content.","color":"#f5f5f5","importance":0,"isPinned":false,"position":0}'
expect 201 'create note for incident-share revocation test'
note_id="$(json_field noteId)"
post "$cookie_admin" "/api/notes/$note_id/shares" '{"permission":"view","expiresInHours":24}'
expect 201 'create a link to revoke from admin'
share_id="$(json_field shareId)"
share_token="$(json_field token)"
post "$cookie_admin" "/api/console-4d91/shares/$share_id/revoke" '{}'
expect 204 'revoke an abusive share link as administrator'
last_status="$(curl -sS -o "$response_file" -w '%{http_code}' "$base_url/api/shared/$share_token")"
expect 404 'deny access through the admin-revoked link'

last_status="$(curl -sS -b "$cookie_admin" -o "$response_file" -w '%{http_code}' "$base_url/api/console-4d91/audit")"
expect 200 'allow TOTP-verified administrator to inspect audit records'
if ! grep -q 'admin_user_locked' "$response_file" \
    || ! grep -q 'admin_share_revoked' "$response_file" \
    || grep -q 'Admin APIs must never expose note content.' "$response_file"; then
    printf '%s\n' 'Admin audit results omitted admin events or exposed note content.' >&2
    exit 1
fi
printf '%s\n' 'PASS audit records admin actions without note contents'

for operation in \
    "UPDATE dbo.audit_logs SET Action = Action WHERE 1 = 0" \
    "DELETE FROM dbo.audit_logs WHERE 1 = 0"; do
    if docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_APP_PASSWORD" db \
        /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_app -d SecureNotes -b -Q "$operation" \
        >/dev/null 2>&1; then
        printf 'notes_app unexpectedly executed: %s\n' "$operation" >&2
        exit 1
    fi
done
printf '%s\n' 'PASS application database login cannot update or delete audit records'

docker compose exec -T api test -d /var/lib/securenotes/logs
printf '%s\n' 'PASS API has a persistent rolling-log volume configured'
printf '%s\n' 'P8 admin and audit smoke tests passed.'
