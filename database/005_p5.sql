:ON ERROR EXIT

USE SecureNotes;
GO

IF OBJECT_ID(N'dbo.external_logins', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.external_logins
    (
        ExternalLoginId bigint IDENTITY(1,1) NOT NULL,
        UserId uniqueidentifier NOT NULL,
        Provider varchar(32) NOT NULL,
        ProviderSubject nvarchar(255) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_external_logins_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_external_logins PRIMARY KEY (ExternalLoginId),
        CONSTRAINT FK_external_logins_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId) ON DELETE CASCADE,
        CONSTRAINT CK_external_logins_Provider CHECK (Provider = 'google'),
        CONSTRAINT UQ_external_logins_Provider_Subject UNIQUE (Provider, ProviderSubject),
        CONSTRAINT UQ_external_logins_User_Provider UNIQUE (UserId, Provider)
    );
END;
GO

CREATE OR ALTER PROCEDURE app.GetGoogleUserBySubject
    @Subject nvarchar(255)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT u.UserId, u.Email, u.NormalizedEmail, u.PasswordHash, u.PasswordSalt,
           u.PasswordIterations, u.DekCiphertext, u.Role, u.FailedCount,
           u.LockedUntil, u.IsDisabled, u.CreatedAt, u.UpdatedAt
    FROM dbo.external_logins e
    INNER JOIN dbo.users u ON u.UserId = e.UserId
    WHERE e.Provider = 'google' AND e.ProviderSubject = @Subject;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateGoogleUser
    @Email nvarchar(320),
    @Subject nvarchar(255),
    @DekCiphertext varbinary(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @NormalizedEmail nvarchar(320) = LOWER(LTRIM(RTRIM(@Email)));
    DECLARE @UserId uniqueidentifier = NEWID();

    IF @NormalizedEmail = N'' OR @Subject = N'' OR @DekCiphertext IS NULL
        THROW 50001, 'Verified Google account data is required.', 1;

    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
        WHERE NormalizedEmail = @NormalizedEmail
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50002, 'An account with this email already exists.', 1;
    END;

    IF EXISTS
    (
        SELECT 1 FROM dbo.external_logins WITH (UPDLOCK, HOLDLOCK)
        WHERE Provider = 'google' AND ProviderSubject = @Subject
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50003, 'This Google identity is already linked.', 1;
    END;

    INSERT INTO dbo.users
        (UserId, Email, NormalizedEmail, PasswordHash, PasswordSalt, PasswordIterations, DekCiphertext)
    VALUES
        (@UserId, LTRIM(RTRIM(@Email)), @NormalizedEmail, NULL, NULL, NULL, @DekCiphertext);

    INSERT INTO dbo.external_logins (UserId, Provider, ProviderSubject)
    VALUES (@UserId, 'google', @Subject);

    COMMIT TRANSACTION;

    SELECT UserId, Email, NormalizedEmail, PasswordHash, PasswordSalt,
           PasswordIterations, DekCiphertext, Role, FailedCount,
           LockedUntil, IsDisabled, CreatedAt, UpdatedAt
    FROM dbo.users
    WHERE UserId = @UserId;
END;
GO

CREATE OR ALTER PROCEDURE app.LinkGoogleLogin
    @UserId uniqueidentifier,
    @Subject nvarchar(255)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS
    (
        SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
        WHERE UserId = @UserId AND IsDisabled = 0 AND PasswordHash IS NOT NULL
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50004, 'A password-authenticated user is required to link Google.', 1;
    END;

    IF EXISTS
    (
        SELECT 1 FROM dbo.external_logins WITH (UPDLOCK, HOLDLOCK)
        WHERE UserId = @UserId AND Provider = 'google'
    )
       OR EXISTS
    (
        SELECT 1 FROM dbo.external_logins WITH (UPDLOCK, HOLDLOCK)
        WHERE Provider = 'google' AND ProviderSubject = @Subject
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50003, 'This Google identity cannot be linked.', 1;
    END;

    INSERT INTO dbo.external_logins (UserId, Provider, ProviderSubject)
    VALUES (@UserId, 'google', @Subject);

    COMMIT TRANSACTION;

    SELECT UserId, Email, NormalizedEmail, PasswordHash, PasswordSalt,
           PasswordIterations, DekCiphertext, Role, FailedCount,
           LockedUntil, IsDisabled, CreatedAt, UpdatedAt
    FROM dbo.users
    WHERE UserId = @UserId;
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
