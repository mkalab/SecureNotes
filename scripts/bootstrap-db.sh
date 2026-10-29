#!/bin/bash
set -euo pipefail

sqlcmd=/opt/mssql-tools18/bin/sqlcmd
work_file="$(mktemp)"
trap 'rm -f "$work_file"' EXIT

while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line//__NOTES_MIGRATOR_PASSWORD__/$NOTES_MIGRATOR_PASSWORD}"
    line="${line//__NOTES_APP_PASSWORD__/$NOTES_APP_PASSWORD}"
    printf '%s\n' "$line" >> "$work_file"
done < /bootstrap/001_schema.sql

if SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_migrator -b -Q 'SELECT 1' >/dev/null 2>&1
then
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i "$work_file"
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/002_p2.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/003_p3.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/004_p4.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/005_p5.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/006_p6.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/007_p7.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/008_p8.sql
else
    SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d master -U sa -b \
        -i "$work_file"
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/002_p2.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/003_p3.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/004_p4.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/005_p5.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/006_p6.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/007_p7.sql
    SQLCMDPASSWORD="$NOTES_MIGRATOR_PASSWORD" "$sqlcmd" \
        -C -S db,1433 -d SecureNotes -U notes_migrator -b \
        -i /bootstrap/008_p8.sql
fi

if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "SELECT TOP (1) UserId FROM dbo.users;" >/dev/null 2>&1
then
    printf '%s\n' 'P1 permission check failed: notes_app can read dbo.users directly.' >&2
    exit 1
fi
if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "UPDATE dbo.audit_logs SET Action = Action WHERE 1 = 0;" >/dev/null 2>&1
then
    printf '%s\n' 'P8 permission check failed: notes_app can modify audit logs.' >&2
    exit 1
fi
if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "SELECT TOP (1) UserId FROM dbo.external_logins;" >/dev/null 2>&1
then
    printf '%s\n' 'P5 permission check failed: notes_app can read dbo.external_logins directly.' >&2
    exit 1
fi
if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "SELECT TOP (1) UserId FROM dbo.note_attachments;" >/dev/null 2>&1
then
    printf '%s\n' 'P6 permission check failed: notes_app can read dbo.note_attachments directly.' >&2
    exit 1
fi
if SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "SELECT TOP (1) UserId FROM dbo.share_links;" >/dev/null 2>&1
then
    printf '%s\n' 'P7 permission check failed: notes_app can read dbo.share_links directly.' >&2
    exit 1
fi

SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "EXEC app.GetUserByEmail @Email = N'p1-permission-check@invalid.local';" >/dev/null
SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "BEGIN TRANSACTION; EXEC app.CreateUser @Email = N'p1-create-check@invalid.local', @DekCiphertext = 0x01; ROLLBACK TRANSACTION;" >/dev/null
SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "EXEC app.GetGoogleUserBySubject @Subject = N'p5-permission-check';" >/dev/null
SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "EXEC app.ListNoteAttachments @UserId = '00000000-0000-0000-0000-000000000000', @NoteId = '00000000-0000-0000-0000-000000000000';" >/dev/null
SQLCMDPASSWORD="$NOTES_APP_PASSWORD" "$sqlcmd" \
    -C -S db,1433 -d SecureNotes -U notes_app -b \
    -Q "EXEC app.ListNoteShares @UserId = '00000000-0000-0000-0000-000000000000', @NoteId = '00000000-0000-0000-0000-000000000000';" >/dev/null
printf '%s\n' 'Database schema is ready; notes_app is denied direct table reads and can execute the required procedures.'
