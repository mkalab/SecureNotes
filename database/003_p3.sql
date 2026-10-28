:ON ERROR EXIT

USE SecureNotes;
GO

IF OBJECT_ID(N'dbo.user_sessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.user_sessions
    (
        SessionIdHash binary(32) NOT NULL,
        UserId uniqueidentifier NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL,
        LastSeenAtUtc datetime2(3) NOT NULL,
        ExpiresAtUtc datetime2(3) NOT NULL,
        SecurityViolationCount int NOT NULL CONSTRAINT DF_user_sessions_SecurityViolationCount DEFAULT 0,
        ViolationWindowStartUtc datetime2(3) NULL,
        CONSTRAINT PK_user_sessions PRIMARY KEY (SessionIdHash),
        CONSTRAINT UQ_user_sessions_UserId UNIQUE (UserId),
        CONSTRAINT FK_user_sessions_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId) ON DELETE CASCADE
    );
END;
GO

IF COL_LENGTH(N'dbo.user_sessions', N'SecurityViolationCount') IS NULL
BEGIN
    ALTER TABLE dbo.user_sessions
        ADD SecurityViolationCount int NOT NULL
            CONSTRAINT DF_user_sessions_SecurityViolationCount DEFAULT 0;
END;
GO

IF COL_LENGTH(N'dbo.user_sessions', N'ViolationWindowStartUtc') IS NULL
BEGIN
    ALTER TABLE dbo.user_sessions ADD ViolationWindowStartUtc datetime2(3) NULL;
END;
GO

IF OBJECT_ID(N'dbo.FK_audit_logs_users', N'F') IS NOT NULL
BEGIN
    ALTER TABLE dbo.audit_logs DROP CONSTRAINT FK_audit_logs_users;
END;
GO

ALTER TABLE dbo.audit_logs
    ADD CONSTRAINT FK_audit_logs_users FOREIGN KEY (UserId)
        REFERENCES dbo.users(UserId) ON DELETE SET NULL;
GO

CREATE OR ALTER PROCEDURE app.CreateUserSession
    @UserId uniqueidentifier,
    @SessionIdHash binary(32),
    @ExpiresAtUtc datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50020, 'The user does not exist.', 1;
    END;

    DELETE FROM dbo.user_sessions WHERE UserId = @UserId;

    INSERT INTO dbo.user_sessions (SessionIdHash, UserId, CreatedAtUtc, LastSeenAtUtc, ExpiresAtUtc)
    VALUES (@SessionIdHash, @UserId, SYSUTCDATETIME(), SYSUTCDATETIME(), @ExpiresAtUtc);

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE app.ValidateAndTouchUserSession
    @SessionIdHash binary(32),
    @NowUtc datetime2(3),
    @IdleTimeoutMinutes int
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.user_sessions
    SET LastSeenAtUtc = @NowUtc
    WHERE SessionIdHash = @SessionIdHash
      AND ExpiresAtUtc > @NowUtc
      AND LastSeenAtUtc > DATEADD(MINUTE, -@IdleTimeoutMinutes, @NowUtc);

    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit);
END;
GO

CREATE OR ALTER PROCEDURE app.RevokeUserSession
    @SessionIdHash binary(32)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM dbo.user_sessions WHERE SessionIdHash = @SessionIdHash;
END;
GO

CREATE OR ALTER PROCEDURE app.RecordSessionSecurityViolation
    @SessionIdHash binary(32),
    @NowUtc datetime2(3),
    @Action varchar(80),
    @IpAddress varchar(45) = NULL,
    @UserAgent nvarchar(512) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserId uniqueidentifier;
    DECLARE @ViolationCount int;
    DECLARE @WindowStartUtc datetime2(3);
    DECLARE @Revoked bit = 0;

    BEGIN TRANSACTION;

    SELECT @UserId = UserId,
           @ViolationCount = SecurityViolationCount,
           @WindowStartUtc = ViolationWindowStartUtc
    FROM dbo.user_sessions WITH (UPDLOCK, ROWLOCK)
    WHERE SessionIdHash = @SessionIdHash;

    IF @UserId IS NOT NULL
    BEGIN
        IF @WindowStartUtc IS NULL OR @WindowStartUtc <= DATEADD(MINUTE, -5, @NowUtc)
        BEGIN
            SET @WindowStartUtc = @NowUtc;
            SET @ViolationCount = 1;
        END
        ELSE
        BEGIN
            SET @ViolationCount += 1;
        END;

        IF @ViolationCount >= 5
        BEGIN
            DELETE FROM dbo.user_sessions WHERE SessionIdHash = @SessionIdHash;
            EXEC app.AddAuditLog
                @UserId = @UserId,
                @Action = @Action,
                @ResourceType = 'session',
                @IpAddress = @IpAddress,
                @UserAgent = @UserAgent;
            SET @Revoked = 1;
        END
        ELSE
        BEGIN
            UPDATE dbo.user_sessions
            SET SecurityViolationCount = @ViolationCount,
                ViolationWindowStartUtc = @WindowStartUtc
            WHERE SessionIdHash = @SessionIdHash;
        END;
    END;

    COMMIT TRANSACTION;
    SELECT @Revoked;
END;
GO

CREATE OR ALTER PROCEDURE app.RecordFailedLogin
    @Email nvarchar(320)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NowUtc datetime2(3) = SYSUTCDATETIME();
    DECLARE @NormalizedEmail nvarchar(320) = LOWER(LTRIM(RTRIM(@Email)));

    UPDATE dbo.users
    SET FailedCount = CASE WHEN LockedUntil IS NOT NULL AND LockedUntil > @NowUtc THEN FailedCount ELSE FailedCount + 1 END,
        LockedUntil = CASE
            WHEN LockedUntil IS NOT NULL AND LockedUntil > @NowUtc THEN LockedUntil
            WHEN FailedCount + 1 >= 5 THEN DATEADD(MINUTE, 15, @NowUtc)
            ELSE NULL
        END,
        UpdatedAt = @NowUtc
    WHERE NormalizedEmail = @NormalizedEmail;
END;
GO

CREATE OR ALTER PROCEDURE app.ResetFailedLogin
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.users
    SET FailedCount = 0, LockedUntil = NULL, UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId;
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
