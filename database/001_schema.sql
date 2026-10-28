:ON ERROR EXIT

IF DB_ID(N'SecureNotes') IS NULL
BEGIN
    CREATE DATABASE SecureNotes;
END;
GO

IF IS_SRVROLEMEMBER(N'sysadmin') = 1
BEGIN
    IF SUSER_ID(N'notes_migrator') IS NULL
    BEGIN
        CREATE LOGIN notes_migrator
            WITH PASSWORD = N'__NOTES_MIGRATOR_PASSWORD__',
                 CHECK_POLICY = ON,
                 CHECK_EXPIRATION = OFF,
                 DEFAULT_DATABASE = SecureNotes;
    END;

    IF SUSER_ID(N'notes_app') IS NULL
    BEGIN
        CREATE LOGIN notes_app
            WITH PASSWORD = N'__NOTES_APP_PASSWORD__',
                 CHECK_POLICY = ON,
                 CHECK_EXPIRATION = OFF,
                 DEFAULT_DATABASE = SecureNotes;
    END;
END;
GO

USE SecureNotes;
GO

IF USER_ID(N'notes_migrator') IS NULL
BEGIN
    CREATE USER notes_migrator FOR LOGIN notes_migrator;
    ALTER ROLE db_owner ADD MEMBER notes_migrator;
END;
GO

IF USER_ID(N'notes_app') IS NULL
BEGIN
    CREATE USER notes_app FOR LOGIN notes_app;
END;
GO

IF SCHEMA_ID(N'app') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA app AUTHORIZATION dbo;');
END;
GO

IF OBJECT_ID(N'dbo.users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.users
    (
        UserId uniqueidentifier NOT NULL CONSTRAINT DF_users_UserId DEFAULT NEWID(),
        Email nvarchar(320) NOT NULL,
        NormalizedEmail nvarchar(320) NOT NULL,
        PasswordHash varbinary(64) NULL,
        PasswordSalt varbinary(32) NULL,
        PasswordIterations int NULL,
        DekCiphertext varbinary(max) NULL,
        Role varchar(16) NOT NULL CONSTRAINT DF_users_Role DEFAULT 'user',
        FailedCount int NOT NULL CONSTRAINT DF_users_FailedCount DEFAULT 0,
        LockedUntil datetime2(3) NULL,
        IsDisabled bit NOT NULL CONSTRAINT DF_users_IsDisabled DEFAULT 0,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_users_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_users_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_users PRIMARY KEY (UserId),
        CONSTRAINT UQ_users_NormalizedEmail UNIQUE (NormalizedEmail),
        CONSTRAINT CK_users_PasswordCredentials CHECK
        (
            (PasswordHash IS NULL AND PasswordSalt IS NULL AND PasswordIterations IS NULL)
            OR
            (PasswordHash IS NOT NULL AND PasswordSalt IS NOT NULL AND PasswordIterations > 0)
        ),
        CONSTRAINT CK_users_Role CHECK (Role IN ('user', 'admin')),
        CONSTRAINT CK_users_FailedCount CHECK (FailedCount >= 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.categories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.categories
    (
        CategoryId uniqueidentifier NOT NULL CONSTRAINT DF_categories_CategoryId DEFAULT NEWID(),
        UserId uniqueidentifier NOT NULL,
        Name nvarchar(100) NOT NULL,
        NormalizedName nvarchar(100) NOT NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_categories_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_categories PRIMARY KEY (CategoryId),
        CONSTRAINT FK_categories_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId),
        CONSTRAINT UQ_categories_User_NormalizedName UNIQUE (UserId, NormalizedName)
    );
END;
GO

IF OBJECT_ID(N'dbo.notes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.notes
    (
        NoteId uniqueidentifier NOT NULL CONSTRAINT DF_notes_NoteId DEFAULT NEWID(),
        UserId uniqueidentifier NOT NULL,
        CategoryId uniqueidentifier NULL,
        ParentId uniqueidentifier NULL,
        Title nvarchar(200) NOT NULL,
        Content nvarchar(max) NOT NULL,
        SourceUrl nvarchar(2048) NULL,
        Color char(7) NOT NULL CONSTRAINT DF_notes_Color DEFAULT '#FFFFFF',
        Importance tinyint NOT NULL CONSTRAINT DF_notes_Importance DEFAULT 0,
        IsPinned bit NOT NULL CONSTRAINT DF_notes_IsPinned DEFAULT 0,
        Position int NOT NULL CONSTRAINT DF_notes_Position DEFAULT 0,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_notes_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_notes_UpdatedAt DEFAULT SYSUTCDATETIME(),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_notes PRIMARY KEY (NoteId),
        CONSTRAINT FK_notes_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId),
        CONSTRAINT FK_notes_categories FOREIGN KEY (CategoryId) REFERENCES dbo.categories(CategoryId),
        CONSTRAINT FK_notes_parent FOREIGN KEY (ParentId) REFERENCES dbo.notes(NoteId),
        CONSTRAINT CK_notes_Importance CHECK (Importance BETWEEN 0 AND 4)
    );
END;
GO

IF OBJECT_ID(N'dbo.note_items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.note_items
    (
        NoteItemId uniqueidentifier NOT NULL CONSTRAINT DF_note_items_NoteItemId DEFAULT NEWID(),
        NoteId uniqueidentifier NOT NULL,
        Title nvarchar(1000) NOT NULL,
        Position int NOT NULL,
        IsCompleted bit NOT NULL CONSTRAINT DF_note_items_IsCompleted DEFAULT 0,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_note_items_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_note_items PRIMARY KEY (NoteItemId),
        CONSTRAINT FK_note_items_notes FOREIGN KEY (NoteId) REFERENCES dbo.notes(NoteId) ON DELETE CASCADE
    );
END;
GO

IF OBJECT_ID(N'dbo.audit_logs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.audit_logs
    (
        AuditLogId bigint IDENTITY(1,1) NOT NULL,
        UserId uniqueidentifier NULL,
        Action varchar(80) NOT NULL,
        ResourceType varchar(80) NULL,
        ResourceId uniqueidentifier NULL,
        IpAddress varchar(45) NULL,
        UserAgent nvarchar(512) NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_audit_logs_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_audit_logs PRIMARY KEY (AuditLogId),
        CONSTRAINT FK_audit_logs_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId)
    );
END;
GO

CREATE OR ALTER PROCEDURE app.CreateUser
    @Email nvarchar(320),
    @PasswordHash varbinary(64) = NULL,
    @PasswordSalt varbinary(32) = NULL,
    @PasswordIterations int = NULL,
    @DekCiphertext varbinary(max) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NormalizedEmail nvarchar(320) = LOWER(LTRIM(RTRIM(@Email)));

    IF @NormalizedEmail = N''
        THROW 50001, 'Email is required.', 1;

    IF EXISTS (SELECT 1 FROM dbo.users WHERE NormalizedEmail = @NormalizedEmail)
        THROW 50002, 'A user with this email already exists.', 1;

    INSERT INTO dbo.users (Email, NormalizedEmail, PasswordHash, PasswordSalt, PasswordIterations, DekCiphertext)
    VALUES (LTRIM(RTRIM(@Email)), @NormalizedEmail, @PasswordHash, @PasswordSalt, @PasswordIterations, @DekCiphertext);

    SELECT UserId, Email, NormalizedEmail, PasswordHash, PasswordSalt, PasswordIterations,
           DekCiphertext, Role, FailedCount, LockedUntil, IsDisabled, CreatedAt, UpdatedAt
    FROM dbo.users
    WHERE NormalizedEmail = @NormalizedEmail;
END;
GO

CREATE OR ALTER PROCEDURE app.GetUserByEmail
    @Email nvarchar(320)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NormalizedEmail nvarchar(320) = LOWER(LTRIM(RTRIM(@Email)));

    SELECT UserId, Email, NormalizedEmail, PasswordHash, PasswordSalt, PasswordIterations,
           DekCiphertext, Role, FailedCount, LockedUntil, IsDisabled, CreatedAt, UpdatedAt
    FROM dbo.users
    WHERE NormalizedEmail = @NormalizedEmail;
END;
GO

CREATE OR ALTER PROCEDURE app.AddAuditLog
    @UserId uniqueidentifier = NULL,
    @Action varchar(80),
    @ResourceType varchar(80) = NULL,
    @ResourceId uniqueidentifier = NULL,
    @IpAddress varchar(45) = NULL,
    @UserAgent nvarchar(512) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.audit_logs (UserId, Action, ResourceType, ResourceId, IpAddress, UserAgent)
    VALUES (@UserId, @Action, @ResourceType, @ResourceId, @IpAddress, @UserAgent);
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO notes_app;
GO

IF ORIGINAL_LOGIN() = N'sa' AND EXISTS
(
    SELECT 1
    FROM sys.sql_logins
    WHERE name = N'sa' AND is_disabled = 0
)
BEGIN
    ALTER LOGIN sa DISABLE;
END;
GO
