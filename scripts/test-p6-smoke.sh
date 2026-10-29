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
email_one="p6-smoke-${run_id}-a@example.test"
email_two="p6-smoke-${run_id}-b@example.test"
test_password='SecureNotes-P6-Smoke!2026'
cookie_one="$(mktemp)"
cookie_two="$(mktemp)"
response_file="$(mktemp)"
response_headers="$(mktemp)"
text_file="$(mktemp --suffix=.txt)"
bad_mime_file="$(mktemp --suffix=.txt)"
unsupported_file="$(mktemp --suffix=.exe)"
oversized_file="$(mktemp --suffix=.txt)"
markdown_file="$(mktemp --suffix=.md)"
pdf_file="$(mktemp --suffix=.pdf)"
png_file="$(mktemp --suffix=.png)"
jpeg_file="$(mktemp --suffix=.jpg)"
download_file="$(mktemp)"
last_status=
note_id=

cleanup() {
    if [[ -n "$note_id" ]]; then
        local token
        token="$(token_for "$cookie_one" 2>/dev/null || true)"
        if [[ -n "$token" ]]; then
            curl -sS -b "$cookie_one" -c "$cookie_one" -o /dev/null \
                -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
                "$base_url/api/notes/$note_id/delete" || true
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
        printf '%s\n' 'Warning: database cleanup for P6 smoke-test accounts failed.' >&2
    fi

    rm -f "$cookie_one" "$cookie_two" "$response_file" "$response_headers" \
        "$text_file" "$bad_mime_file" "$unsupported_file" "$oversized_file" "$markdown_file" \
        "$pdf_file" "$png_file" "$jpeg_file" "$download_file"
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
    local token
    token="$(token_for "$jar")"
    last_status="$(curl -sS -b "$jar" -c "$jar" -o "$response_file" -D "$response_headers" \
        -w '%{http_code}' -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
        -H 'Content-Type: application/json' --data "$body" "$base_url$path")"
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

request_post "$cookie_one" /api/auth/register \
    "{\"email\":\"$email_one\",\"password\":\"$test_password\"}"
expect_status 201 'register attachment owner'
request_post "$cookie_two" /api/auth/register \
    "{\"email\":\"$email_two\",\"password\":\"$test_password\"}"
expect_status 201 'register second account'

request_post "$cookie_one" /api/notes \
    '{"title":"P6 attachment smoke note","content":"attachment test","color":"#fff8c5","importance":1,"isPinned":false,"position":0}'
expect_status 201 'create attachment note'
note_id="$(json_field noteId)"

printf '%s\n' 'P6 encrypted attachment smoke content' > "$text_file"
printf '%s\n' 'MIME mismatch test' > "$bad_mime_file"
printf '%s\n' 'Unsupported extension test' > "$unsupported_file"
printf '%s\n' '# Markdown attachment smoke test' > "$markdown_file"
cp frontend/src/assets/hero.png "$png_file"
node -e 'require("node:fs").writeFileSync(process.argv[1], Buffer.from(process.argv[2], "base64"))' \
    "$jpeg_file" '/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDhKKKKoD//2Q=='
node - "$pdf_file" <<'NODE'
const fs = require('node:fs')
const objects = [
  '<< /Type /Catalog /Pages 2 0 R >>',
  '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
  '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>',
]
let pdf = '%PDF-1.4\n'
const offsets = []
objects.forEach((object, index) => {
  offsets.push(Buffer.byteLength(pdf))
  pdf += `${index + 1} 0 obj\n${object}\nendobj\n`
})
const xrefOffset = Buffer.byteLength(pdf)
pdf += 'xref\n0 4\n0000000000 65535 f \n'
offsets.forEach((offset) => {
  pdf += `${String(offset).padStart(10, '0')} 00000 n \n`
})
pdf += `trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n${xrefOffset}\n%%EOF\n`
fs.writeFileSync(process.argv[2], pdf)
NODE
head -c 10485761 /dev/zero > "$oversized_file"

upload_file() {
    local jar="$1"
    local path="$2"
    local file="$3"
    local media_type="$4"
    local token
    token="$(token_for "$jar")"
    last_status="$(curl -sS -b "$jar" -c "$jar" -o "$response_file" -D "$response_headers" \
        -w '%{http_code}' -X POST -H "Origin: $origin" -H "X-CSRF-TOKEN: $token" \
        -F "file=@$file;type=$media_type" "$base_url$path")"
}

upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$text_file" text/plain
expect_status 201 'upload valid UTF-8 text attachment'
attachment_id="$(json_field attachmentId)"
storage_id="$(docker compose exec -T -e "SQLCMDPASSWORD=$NOTES_MIGRATOR_PASSWORD" db \
    /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U notes_migrator -d SecureNotes -b -h -1 -W \
    -Q "SET NOCOUNT ON; SELECT StorageId FROM dbo.note_attachments WHERE AttachmentId = '$attachment_id' AND Status = 1;" |
    tr -d '[:space:]')"
if [[ ! "$storage_id" =~ ^[0-9a-f]{64}$ ]]; then
    printf '%s\n' 'Could not resolve the attachment storage identifier from the DB.' >&2
    exit 1
fi
storage_file="/var/lib/securenotes/attachments/$storage_id.blob"
if ! docker compose exec -T api test -f "$storage_file"; then
    printf '%s\n' 'Encrypted attachment file is missing from persistent storage.' >&2
    exit 1
fi
if docker compose exec -T api grep -aFq 'P6 encrypted attachment smoke content' "$storage_file"; then
    printf '%s\n' 'Attachment plaintext was found in the storage volume.' >&2
    exit 1
fi
printf '%s\n' 'PASS attachment is stored as ciphertext outside the database'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$markdown_file" text/markdown
expect_status 201 'upload valid Markdown attachment'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$pdf_file" application/pdf
expect_status 201 'upload structurally valid PDF attachment'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$png_file" image/png
expect_status 201 'upload valid PNG attachment'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$jpeg_file" image/jpeg
expect_status 201 'upload valid JPEG attachment'

last_status="$(curl -sS -b "$cookie_one" -o "$download_file" -D "$response_headers" \
    -w '%{http_code}' "$base_url/api/notes/$note_id/attachments/$attachment_id/download")"
expect_status 200 'download owned attachment'
cmp "$text_file" "$download_file"
grep -qi '^Content-Disposition: attachment' "$response_headers"
grep -qi '^X-Content-Type-Options: nosniff' "$response_headers"
grep -qi '^Cache-Control: no-store' "$response_headers"
printf '%s\n' 'PASS download returns original bytes with safe response headers'

last_status="$(curl -sS -b "$cookie_two" -o "$response_file" -w '%{http_code}' \
    "$base_url/api/notes/$note_id/attachments/$attachment_id/download")"
expect_status 404 'deny cross-account attachment download'

upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$bad_mime_file" application/pdf
expect_status 400 'reject extension and MIME mismatch'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$unsupported_file" application/octet-stream
expect_status 400 'reject unsupported file extension'
upload_file "$cookie_one" "/api/notes/$note_id/attachments" "$oversized_file" text/plain
expect_status 413 'reject attachment above 10 MiB'

request_post "$cookie_one" "/api/notes/$note_id/attachments/$attachment_id/delete" '{}'
expect_status 204 'delete owned attachment'
if docker compose exec -T api test -e "$storage_file"; then
    printf '%s\n' 'Encrypted attachment file remained after deletion.' >&2
    exit 1
fi
printf '%s\n' 'PASS deleting attachment removes its encrypted file'
last_status="$(curl -sS -b "$cookie_one" -o "$response_file" -w '%{http_code}' \
    "$base_url/api/notes/$note_id/attachments/$attachment_id/download")"
expect_status 404 'hide deleted attachment'

last_status="$(curl -sS -b "$cookie_two" -o "$response_file" -w '%{http_code}' \
    "$base_url/api/notes/$note_id/attachments")"
expect_status 404 'deny cross-account attachment listing'

printf '%s\n' 'P6 attachment smoke tests passed.'
