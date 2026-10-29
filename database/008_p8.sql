:ON ERROR EXIT

USE SecureNotes;
GO

IF COL_LENGTH(N'dbo.users', N'TwoFactorEnabled') IS NULL
BEGIN
    ALTER TABLE dbo.users ADD TwoFactorEnabled bit NOT NULL
        CONSTRAINT DF_users_TwoFactorEnabled DEFAULT 0;
END;
GO

IF COL_LENGTH(N'dbo.users', N'TwoFactorSecret') IS NULL
BEGIN
    ALTER TABLE dbo.users ADD TwoFactorSecret varbinary(128) NULL;
END;
GO

IF COL_LENGTH(N'dbo.users', N'TwoFactorPendingSecret') IS NULL
BEGIN
    ALTER TABLE dbo.users ADD TwoFactorPendingSecret varbinary(128) NULL;
END;
GO

IF COL_LENGTH(N'dbo.users', N'TwoFactorLastStep') IS NULL
BEGIN
    ALTER TABLE dbo.users ADD TwoFactorLastStep bigint NULL;
END;
GO

IF COL_LENGTH(N'dbo.user_sessions', N'TwoFactorVerified') IS NULL
BEGIN
    ALTER TABLE dbo.user_sessions ADD TwoFactorVerified bit NOT NULL
        CONSTRAINT DF_user_sessions_TwoFactorVerified DEFAULT 0;
END;
GO

IF OBJECT_ID(N'dbo.two_factor_recovery_codes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.two_factor_recovery_codes
    (
        UserId uniqueidentifier NOT NULL,
        CodeHash binary(32) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_two_factor_recovery_codes_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_two_factor_recovery_codes PRIMARY KEY (UserId, CodeHash),
        CONSTRAINT FK_two_factor_recovery_codes_users FOREIGN KEY (UserId)
            REFERENCES dbo.users(UserId) ON DELETE CASCADE
    );
END;
GO

IF OBJECT_ID(N'dbo.two_factor_challenges', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.two_factor_challenges
    (
        ChallengeHash binary(32) NOT NULL,
        UserId uniqueidentifier NOT NULL,
        ExpiresAtUtc datetime2(3) NOT NULL,
        FailedAttempts tinyint NOT NULL CONSTRAINT DF_two_factor_challenges_FailedAttempts DEFAULT 0,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_two_factor_challenges_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_two_factor_challenges PRIMARY KEY (ChallengeHash),
        CONSTRAINT FK_two_factor_challenges_users FOREIGN KEY (UserId)
            REFERENCES dbo.users(UserId) ON DELETE CASCADE
    );
    CREATE INDEX IX_two_factor_challenges_ExpiresAtUtc ON dbo.two_factor_challenges(ExpiresAtUtc);
END;
GO

IF COL_LENGTH(N'dbo.two_factor_challenges', N'AuthProvider') IS NULL
BEGIN
    ALTER TABLE dbo.two_factor_challenges ADD AuthProvider varchar(16) NOT NULL
        CONSTRAINT DF_two_factor_challenges_AuthProvider DEFAULT 'password';
END;
GO

CREATE OR ALTER PROCEDURE app.GetUserTwoFactorData
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TwoFactorEnabled, TwoFactorSecret, TwoFactorPendingSecret, TwoFactorLastStep
    FROM dbo.users
    WHERE UserId = @UserId AND IsDisabled = 0;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateUserSession
    @UserId uniqueidentifier,
    @SessionIdHash binary(32),
    @ExpiresAtUtc datetime2(3),
    @TwoFactorVerified bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId AND IsDisabled = 0)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50020, 'The user does not exist or is disabled.', 1;
    END;
    DELETE FROM dbo.user_sessions WHERE UserId = @UserId;
    INSERT INTO dbo.user_sessions
        (SessionIdHash, UserId, CreatedAtUtc, LastSeenAtUtc, ExpiresAtUtc, TwoFactorVerified)
    VALUES
        (@SessionIdHash, @UserId, SYSUTCDATETIME(), SYSUTCDATETIME(), @ExpiresAtUtc, @TwoFactorVerified);
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE app.SetPendingTwoFactorSecret
    @UserId uniqueidentifier,
    @Secret varbinary(128)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.users
    SET TwoFactorPendingSecret = @Secret, UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND IsDisabled = 0 AND TwoFactorEnabled = 0;
    SELECT @@ROWCOUNT;
END;
GO

CREATE OR ALTER PROCEDURE app.EnableTwoFactor
    @UserId uniqueidentifier,
    @Secret varbinary(128),
    @LastStep bigint,
    @RecoveryHashes nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
        WHERE UserId = @UserId AND IsDisabled = 0 AND TwoFactorEnabled = 0
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50080, 'Two-factor setup is unavailable.', 1;
    END;

    DELETE FROM dbo.two_factor_recovery_codes WHERE UserId = @UserId;
    INSERT INTO dbo.two_factor_recovery_codes(UserId, CodeHash)
    SELECT @UserId, CONVERT(binary(32), CONVERT(varbinary(128), value, 2))
    FROM OPENJSON(@RecoveryHashes)
    WHERE LEN(value) = 64 AND value NOT LIKE '%[^0-9A-Fa-f]%';

    IF @@ROWCOUNT <> 10
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50081, 'Recovery code set is invalid.', 1;
    END;

    UPDATE dbo.users
    SET TwoFactorEnabled = 1,
        TwoFactorSecret = @Secret,
        TwoFactorPendingSecret = NULL,
        TwoFactorLastStep = @LastStep,
        UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND TwoFactorPendingSecret = @Secret;
    IF @@ROWCOUNT <> 1
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50080, 'Two-factor setup is unavailable.', 1;
    END;

    DELETE FROM dbo.two_factor_challenges WHERE UserId = @UserId;
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE app.DisableTwoFactor
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Updated int = 0;
    UPDATE dbo.users
    SET TwoFactorEnabled = 0,
        TwoFactorSecret = NULL,
        TwoFactorPendingSecret = NULL,
        TwoFactorLastStep = NULL,
        UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND TwoFactorEnabled = 1 AND Role <> 'admin';
    SET @Updated = @@ROWCOUNT;
    DELETE FROM dbo.two_factor_recovery_codes WHERE UserId = @UserId;
    DELETE FROM dbo.two_factor_challenges WHERE UserId = @UserId;
    SELECT @Updated;
END;
GO

CREATE OR ALTER PROCEDURE app.ConsumeManagementTwoFactorTotp
    @UserId uniqueidentifier,
    @Step bigint
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.users
    SET TwoFactorLastStep = @Step
    WHERE UserId = @UserId
      AND TwoFactorEnabled = 1
      AND IsDisabled = 0
      AND (TwoFactorLastStep IS NULL OR TwoFactorLastStep < @Step);
    SELECT CAST(CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END AS bit);
END;
GO

CREATE OR ALTER PROCEDURE app.CreateTwoFactorChallenge
    @UserId uniqueidentifier,
    @ChallengeHash binary(32),
    @ExpiresAtUtc datetime2(3),
    @AuthProvider varchar(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DELETE FROM dbo.two_factor_challenges
    WHERE ExpiresAtUtc <= SYSUTCDATETIME() OR UserId = @UserId;
    INSERT INTO dbo.two_factor_challenges(ChallengeHash, UserId, ExpiresAtUtc, AuthProvider)
    SELECT @ChallengeHash, UserId, @ExpiresAtUtc, @AuthProvider
    FROM dbo.users
    WHERE UserId = @UserId AND IsDisabled = 0 AND TwoFactorEnabled = 1;
    SELECT UserId FROM dbo.two_factor_challenges WHERE ChallengeHash = @ChallengeHash;
END;
GO

CREATE OR ALTER PROCEDURE app.GetTwoFactorChallenge
    @ChallengeHash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT u.UserId, u.Email, u.Role, u.TwoFactorEnabled, u.TwoFactorSecret,
           u.TwoFactorLastStep, u.DekCiphertext, c.AuthProvider
    FROM dbo.two_factor_challenges c
    INNER JOIN dbo.users u ON u.UserId = c.UserId
    WHERE c.ChallengeHash = @ChallengeHash
      AND c.ExpiresAtUtc > SYSUTCDATETIME()
      AND c.FailedAttempts < 5
      AND u.IsDisabled = 0
      AND u.TwoFactorEnabled = 1;
END;
GO

CREATE OR ALTER PROCEDURE app.FailTwoFactorChallenge
    @ChallengeHash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.two_factor_challenges
    SET FailedAttempts = FailedAttempts + 1
    WHERE ChallengeHash = @ChallengeHash AND ExpiresAtUtc > SYSUTCDATETIME();
    DELETE FROM dbo.two_factor_challenges
    WHERE ChallengeHash = @ChallengeHash AND FailedAttempts >= 5;
END;
GO

CREATE OR ALTER PROCEDURE app.ConsumeTwoFactorTotpChallenge
    @ChallengeHash binary(32),
    @UserId uniqueidentifier,
    @Step bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Accepted bit = 0;
    BEGIN TRANSACTION;
    IF EXISTS
    (
        SELECT 1 FROM dbo.two_factor_challenges c WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.users u ON u.UserId = c.UserId
        WHERE c.ChallengeHash = @ChallengeHash
          AND c.UserId = @UserId
          AND c.ExpiresAtUtc > SYSUTCDATETIME()
          AND c.FailedAttempts < 5
          AND u.TwoFactorEnabled = 1
          AND u.IsDisabled = 0
          AND (u.TwoFactorLastStep IS NULL OR u.TwoFactorLastStep < @Step)
    )
    BEGIN
        UPDATE dbo.users SET TwoFactorLastStep = @Step WHERE UserId = @UserId;
        DELETE FROM dbo.two_factor_challenges WHERE ChallengeHash = @ChallengeHash;
        SET @Accepted = 1;
    END;
    COMMIT TRANSACTION;
    SELECT @Accepted;
END;
GO

CREATE OR ALTER PROCEDURE app.ConsumeTwoFactorRecoveryChallenge
    @ChallengeHash binary(32),
    @UserId uniqueidentifier,
    @CodeHash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Accepted bit = 0;
    BEGIN TRANSACTION;
    IF EXISTS
    (
        SELECT 1 FROM dbo.two_factor_challenges c WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN dbo.users u ON u.UserId = c.UserId
        WHERE c.ChallengeHash = @ChallengeHash
          AND c.UserId = @UserId
          AND c.ExpiresAtUtc > SYSUTCDATETIME()
          AND c.FailedAttempts < 5
          AND u.TwoFactorEnabled = 1
          AND u.IsDisabled = 0
    )
    BEGIN
        DELETE FROM dbo.two_factor_recovery_codes
        WHERE UserId = @UserId AND CodeHash = @CodeHash;
        IF @@ROWCOUNT = 1
        BEGIN
            DELETE FROM dbo.two_factor_challenges WHERE ChallengeHash = @ChallengeHash;
            SET @Accepted = 1;
        END;
    END;
    COMMIT TRANSACTION;
    SELECT @Accepted;
END;
GO

CREATE OR ALTER PROCEDURE app.ValidateAndTouchUserSessionWithPrincipal
    @SessionIdHash binary(32),
    @NowUtc datetime2(3),
    @IdleTimeoutMinutes int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE s
    SET LastSeenAtUtc = @NowUtc
    FROM dbo.user_sessions s
    INNER JOIN dbo.users u ON u.UserId = s.UserId
    WHERE s.SessionIdHash = @SessionIdHash
      AND s.ExpiresAtUtc > @NowUtc
      AND s.LastSeenAtUtc > DATEADD(MINUTE, -@IdleTimeoutMinutes, @NowUtc)
      AND u.IsDisabled = 0;
    IF @@ROWCOUNT = 1
    BEGIN
        SELECT u.UserId, u.Email, u.Role, u.TwoFactorEnabled, s.TwoFactorVerified
        FROM dbo.user_sessions s
        INNER JOIN dbo.users u ON u.UserId = s.UserId
        WHERE s.SessionIdHash = @SessionIdHash AND u.IsDisabled = 0;
    END;
END;
GO

CREATE OR ALTER PROCEDURE app.AdminListUsers
    @AdminUserId uniqueidentifier,
    @Offset int,
    @Limit int
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1)
        THROW 50082, 'The requested account action is not permitted.', 1;
    SELECT UserId, Email, Role, IsDisabled, FailedCount, LockedUntil, TwoFactorEnabled, CreatedAt
    FROM dbo.users
    ORDER BY CreatedAt DESC, UserId
    OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
END;
GO

CREATE OR ALTER PROCEDURE app.AdminSetUserDisabled
    @AdminUserId uniqueidentifier,
    @TargetUserId uniqueidentifier,
    @Disabled bit
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @AdminRole varchar(16);
    BEGIN TRANSACTION;
    SELECT @AdminRole = Role FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1;
    IF @AdminRole IS NULL OR @TargetUserId = @AdminUserId
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50082, 'The requested account action is not permitted.', 1;
    END;
    IF @Disabled = 1 AND
    (
        SELECT COUNT(*) FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
        WHERE Role = 'admin' AND IsDisabled = 0 AND TwoFactorEnabled = 1
    ) <= 1 AND EXISTS
    (
        SELECT 1 FROM dbo.users WHERE UserId = @TargetUserId
            AND Role = 'admin' AND IsDisabled = 0 AND TwoFactorEnabled = 1
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50083, 'The last enabled administrator cannot be disabled.', 1;
    END;
    UPDATE dbo.users
    SET IsDisabled = @Disabled,
        FailedCount = CASE WHEN @Disabled = 0 THEN 0 ELSE FailedCount END,
        LockedUntil = CASE WHEN @Disabled = 0 THEN NULL ELSE LockedUntil END,
        UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @TargetUserId;
    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS bit);
        RETURN;
    END;
    IF @Disabled = 1 DELETE FROM dbo.user_sessions WHERE UserId = @TargetUserId;
    COMMIT TRANSACTION;
    SELECT CAST(1 AS bit);
END;
GO

CREATE OR ALTER PROCEDURE app.AdminRevokeUserSessions
    @AdminUserId uniqueidentifier,
    @TargetUserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1)
        THROW 50082, 'The requested account action is not permitted.', 1;
    IF @AdminUserId = @TargetUserId THROW 50082, 'The requested account action is not permitted.', 1;
    DELETE FROM dbo.user_sessions WHERE UserId = @TargetUserId;
    SELECT @@ROWCOUNT;
END;
GO

CREATE OR ALTER PROCEDURE app.AdminListAuditLogs
    @AdminUserId uniqueidentifier,
    @BeforeAuditLogId bigint = NULL,
    @Limit int = 100
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1)
        THROW 50082, 'The requested account action is not permitted.', 1;
    SELECT TOP (@Limit)
        a.AuditLogId, a.UserId, u.Email, a.Action, a.ResourceType, a.ResourceId,
        a.IpAddress, a.UserAgent, a.CreatedAt
    FROM dbo.audit_logs a
    LEFT JOIN dbo.users u ON u.UserId = a.UserId
    WHERE @BeforeAuditLogId IS NULL OR a.AuditLogId < @BeforeAuditLogId
    ORDER BY a.AuditLogId DESC;
END;
GO

CREATE OR ALTER PROCEDURE app.AdminListShares
    @AdminUserId uniqueidentifier,
    @Limit int
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1)
        THROW 50082, 'The requested account action is not permitted.', 1;
    SELECT TOP (@Limit)
        s.ShareId, s.NoteId, s.UserId, u.Email, s.Permission, s.ExpiresAt, s.RevokedAt, s.CreatedAt
    FROM dbo.share_links s
    INNER JOIN dbo.users u ON u.UserId = s.UserId
    ORDER BY s.CreatedAt DESC, s.ShareId;
END;
GO

CREATE OR ALTER PROCEDURE app.AdminRevokeShare
    @AdminUserId uniqueidentifier,
    @ShareId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.users WHERE UserId = @AdminUserId AND IsDisabled = 0 AND Role = 'admin' AND TwoFactorEnabled = 1)
        THROW 50082, 'The requested account action is not permitted.', 1;
    UPDATE dbo.share_links
    SET RevokedAt = SYSUTCDATETIME()
    WHERE ShareId = @ShareId AND RevokedAt IS NULL;
    SELECT @@ROWCOUNT;
END;
GO

CREATE OR ALTER PROCEDURE dbo.PromoteUserToAdmin
    @Email nvarchar(320)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @UserId uniqueidentifier;
    BEGIN TRANSACTION;
    SELECT @UserId = UserId
    FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
    WHERE NormalizedEmail = LOWER(LTRIM(RTRIM(@Email)))
      AND IsDisabled = 0
      AND TwoFactorEnabled = 1
      AND TwoFactorSecret IS NOT NULL;
    IF @UserId IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50084, 'User not found or two-factor authentication is not enabled.', 1;
    END;
    UPDATE dbo.users SET Role = 'admin', UpdatedAt = SYSUTCDATETIME() WHERE UserId = @UserId;
    DELETE FROM dbo.user_sessions WHERE UserId = @UserId;
    EXEC app.AddAuditLog @UserId = @UserId, @Action = 'admin_role_granted', @ResourceType = 'user', @ResourceId = @UserId;
    COMMIT TRANSACTION;
    SELECT UserId, Email FROM dbo.users WHERE UserId = @UserId;
END;
GO

DENY UPDATE, DELETE ON dbo.audit_logs TO notes_app;
GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
