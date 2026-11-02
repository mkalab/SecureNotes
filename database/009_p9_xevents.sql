IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'SecureNotesSecurityAudit')
    DROP EVENT SESSION [SecureNotesSecurityAudit] ON SERVER;
GO

CREATE EVENT SESSION [SecureNotesSecurityAudit]
ON SERVER
ADD EVENT sqlserver.error_reported
(
    WHERE ([error_number] = (18456))
),
ADD EVENT sqlserver.object_created,
ADD EVENT sqlserver.object_altered,
ADD EVENT sqlserver.object_deleted
ADD TARGET package0.event_file
(
    SET filename = N'/var/opt/mssql/data/SecureNotesSecurityAudit.xel',
        max_file_size = (25),
        max_rollover_files = (4)
)
WITH
(
    STARTUP_STATE = ON,
    TRACK_CAUSALITY = ON
);
GO

ALTER EVENT SESSION [SecureNotesSecurityAudit] ON SERVER STATE = START;
GO
