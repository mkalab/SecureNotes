:ON ERROR EXIT

USE SecureNotes;
GO

IF COL_LENGTH(N'dbo.notes', N'TitleEncrypted') IS NULL
    ALTER TABLE dbo.notes ADD TitleEncrypted varbinary(max) NULL;
GO
IF COL_LENGTH(N'dbo.notes', N'ContentEncrypted') IS NULL
    ALTER TABLE dbo.notes ADD ContentEncrypted varbinary(max) NULL;
GO
IF COL_LENGTH(N'dbo.note_items', N'TitleEncrypted') IS NULL
    ALTER TABLE dbo.note_items ADD TitleEncrypted varbinary(max) NULL;
GO

IF OBJECT_ID(N'dbo.CK_notes_EncryptedText', N'C') IS NULL
    ALTER TABLE dbo.notes WITH NOCHECK ADD CONSTRAINT CK_notes_EncryptedText
        CHECK (TitleEncrypted IS NOT NULL AND ContentEncrypted IS NOT NULL);
GO
IF OBJECT_ID(N'dbo.CK_note_items_EncryptedTitle', N'C') IS NULL
    ALTER TABLE dbo.note_items WITH NOCHECK ADD CONSTRAINT CK_note_items_EncryptedTitle
        CHECK (TitleEncrypted IS NOT NULL);
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
    IF @DekCiphertext IS NULL
        THROW 50021, 'A protected user data key is required.', 1;
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

CREATE OR ALTER PROCEDURE app.ListUsersForEncryption
AS
BEGIN
    SET NOCOUNT ON;
    SELECT UserId, DekCiphertext
    FROM dbo.users
    ORDER BY UserId;
END;
GO

CREATE OR ALTER PROCEDURE app.SetUserDekIfMissing
    @UserId uniqueidentifier,
    @DekCiphertext varbinary(max)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.users WITH (UPDLOCK, ROWLOCK)
    SET DekCiphertext = @DekCiphertext, UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND DekCiphertext IS NULL;
    SELECT DekCiphertext FROM dbo.users WHERE UserId = @UserId;
END;
GO

CREATE OR ALTER PROCEDURE app.GetUserDek
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT DekCiphertext FROM dbo.users WHERE UserId = @UserId;
END;
GO

CREATE OR ALTER PROCEDURE app.AcquireP4EncryptionLock
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Result int;
    EXEC @Result = sys.sp_getapplock
        @Resource = N'SecureNotes.P4.EncryptionMigration',
        @LockMode = N'Exclusive',
        @LockOwner = N'Session',
        @LockTimeout = 60000;
    SELECT @Result;
END;
GO

CREATE OR ALTER PROCEDURE app.ReleaseP4EncryptionLock
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Result int;
    EXEC @Result = sys.sp_releaseapplock
        @Resource = N'SecureNotes.P4.EncryptionMigration',
        @LockOwner = N'Session';
    SELECT @Result;
END;
GO

CREATE OR ALTER PROCEDURE app.GetLegacyNotesForEncryption
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT NoteId, Title, Content
    FROM dbo.notes
    WHERE UserId = @UserId
      AND (TitleEncrypted IS NULL OR ContentEncrypted IS NULL);
END;
GO

CREATE OR ALTER PROCEDURE app.EncryptLegacyNote
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @TitleEncrypted varbinary(max),
    @ContentEncrypted varbinary(max)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.notes
    SET TitleEncrypted = @TitleEncrypted,
        ContentEncrypted = @ContentEncrypted,
        Title = N'',
        Content = N'',
        UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND NoteId = @NoteId
      AND (TitleEncrypted IS NULL OR ContentEncrypted IS NULL);
END;
GO

CREATE OR ALTER PROCEDURE app.GetLegacyNoteItemsForEncryption
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.NoteId, i.NoteItemId, i.Title
    FROM dbo.note_items i
    INNER JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.TitleEncrypted IS NULL;
END;
GO

CREATE OR ALTER PROCEDURE app.EncryptLegacyNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @NoteItemId uniqueidentifier,
    @TitleEncrypted varbinary(max)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE i
    SET TitleEncrypted = @TitleEncrypted, Title = N''
    FROM dbo.note_items i
    INNER JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId
      AND i.TitleEncrypted IS NULL;
END;
GO

CREATE OR ALTER PROCEDURE app.FinalizeP4Encryption
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM dbo.notes
        WHERE TitleEncrypted IS NULL OR ContentEncrypted IS NULL OR Title <> N'' OR Content <> N''
    )
        THROW 50022, 'Legacy note content remains unencrypted.', 1;

    IF EXISTS
    (
        SELECT 1 FROM dbo.note_items
        WHERE TitleEncrypted IS NULL OR Title <> N''
    )
        THROW 50023, 'Legacy checklist content remains unencrypted.', 1;

    IF EXISTS (SELECT 1 FROM dbo.users WHERE DekCiphertext IS NULL)
        THROW 50024, 'A user data key is missing.', 1;

END;
GO

CREATE OR ALTER PROCEDURE app.ListNotes
    @UserId uniqueidentifier,
    @Search nvarchar(200) = NULL,
    @CategoryId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT n.NoteId, n.UserId, n.CategoryId, c.Name AS CategoryName, n.ParentId,
           n.TitleEncrypted, n.ContentEncrypted, n.SourceUrl, n.Color, n.Importance, n.IsPinned,
           n.Position, n.CreatedAt, n.UpdatedAt,
           (SELECT COUNT(*) FROM dbo.note_items i WHERE i.NoteId = n.NoteId) AS ItemCount
    FROM dbo.notes n
    LEFT JOIN dbo.categories c ON c.CategoryId = n.CategoryId AND c.UserId = n.UserId
    WHERE n.UserId = @UserId
      AND (@CategoryId IS NULL OR n.CategoryId = @CategoryId)
    ORDER BY n.IsPinned DESC, n.Position, n.UpdatedAt DESC;
END;
GO

CREATE OR ALTER PROCEDURE app.GetNote
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT n.NoteId, n.UserId, n.CategoryId, c.Name AS CategoryName, n.ParentId,
           n.TitleEncrypted, n.ContentEncrypted, n.SourceUrl, n.Color, n.Importance, n.IsPinned,
           n.Position, n.CreatedAt, n.UpdatedAt,
           (SELECT COUNT(*) FROM dbo.note_items i WHERE i.NoteId = n.NoteId) AS ItemCount
    FROM dbo.notes n
    LEFT JOIN dbo.categories c ON c.CategoryId = n.CategoryId AND c.UserId = n.UserId
    WHERE n.UserId = @UserId AND n.NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateNote
    @NoteId uniqueidentifier,
    @UserId uniqueidentifier,
    @TitleEncrypted varbinary(max),
    @ContentEncrypted varbinary(max),
    @CategoryId uniqueidentifier = NULL,
    @ParentId uniqueidentifier = NULL,
    @SourceUrl nvarchar(2048) = NULL,
    @Color char(7) = '#FFFFFF',
    @Importance tinyint = 0,
    @IsPinned bit = 0,
    @Position int = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @CategoryId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM dbo.categories WHERE CategoryId = @CategoryId AND UserId = @UserId)
        THROW 50010, 'Category not found.', 1;
    IF @ParentId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM dbo.notes WHERE NoteId = @ParentId AND UserId = @UserId AND ParentId IS NULL)
        THROW 50011, 'Parent note not found.', 1;

    INSERT INTO dbo.notes
        (NoteId, UserId, CategoryId, ParentId, Title, Content, TitleEncrypted, ContentEncrypted,
         SourceUrl, Color, Importance, IsPinned, Position)
    VALUES
        (@NoteId, @UserId, @CategoryId, @ParentId, N'', N'', @TitleEncrypted, @ContentEncrypted,
         @SourceUrl, @Color, @Importance, @IsPinned, @Position);
    EXEC app.GetNote @UserId = @UserId, @NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.UpdateNote
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @TitleEncrypted varbinary(max),
    @ContentEncrypted varbinary(max),
    @CategoryId uniqueidentifier = NULL,
    @ParentId uniqueidentifier = NULL,
    @SourceUrl nvarchar(2048) = NULL,
    @Color char(7) = '#FFFFFF',
    @Importance tinyint = 0,
    @IsPinned bit = 0,
    @Position int = 0
AS
BEGIN
    SET NOCOUNT ON;
    IF @CategoryId IS NOT NULL AND NOT EXISTS
        (SELECT 1 FROM dbo.categories WHERE CategoryId = @CategoryId AND UserId = @UserId)
        THROW 50010, 'Category not found.', 1;
    IF @ParentId IS NOT NULL AND (@ParentId = @NoteId OR NOT EXISTS
        (SELECT 1 FROM dbo.notes WHERE NoteId = @ParentId AND UserId = @UserId AND ParentId IS NULL))
        THROW 50011, 'Parent note not found.', 1;

    UPDATE dbo.notes
    SET TitleEncrypted = @TitleEncrypted, ContentEncrypted = @ContentEncrypted,
        CategoryId = @CategoryId, ParentId = @ParentId, SourceUrl = @SourceUrl,
        Color = @Color, Importance = @Importance, IsPinned = @IsPinned,
        Position = @Position, UpdatedAt = SYSUTCDATETIME()
    WHERE NoteId = @NoteId AND UserId = @UserId;
    IF @@ROWCOUNT = 0 RETURN;
    EXEC app.GetNote @UserId = @UserId, @NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.ListNoteItems
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.NoteItemId, i.NoteId, i.TitleEncrypted, i.Position, i.IsCompleted, i.CreatedAt
    FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId
    ORDER BY i.Position, i.CreatedAt;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @NoteItemId uniqueidentifier,
    @TitleEncrypted varbinary(max)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM dbo.notes WHERE NoteId = @NoteId AND UserId = @UserId)
        THROW 50011, 'Parent note not found.', 1;

    DECLARE @Created TABLE
    (
        NoteItemId uniqueidentifier,
        NoteId uniqueidentifier,
        TitleEncrypted varbinary(max),
        Position int,
        IsCompleted bit,
        CreatedAt datetime2(3)
    );
    BEGIN TRANSACTION;
    DECLARE @Position int;
    SELECT @Position = ISNULL(MAX(Position), -1) + 1
    FROM dbo.note_items WITH (UPDLOCK, HOLDLOCK)
    WHERE NoteId = @NoteId;
    INSERT INTO dbo.note_items (NoteItemId, NoteId, Title, TitleEncrypted, Position)
    OUTPUT inserted.NoteItemId, inserted.NoteId, inserted.TitleEncrypted, inserted.Position,
           inserted.IsCompleted, inserted.CreatedAt INTO @Created
    VALUES (@NoteItemId, @NoteId, N'', @TitleEncrypted, @Position);
    COMMIT TRANSACTION;
    SELECT NoteItemId, NoteId, TitleEncrypted, Position, IsCompleted, CreatedAt FROM @Created;
END;
GO

CREATE OR ALTER PROCEDURE app.UpdateNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @NoteItemId uniqueidentifier,
    @TitleEncrypted varbinary(max),
    @IsCompleted bit
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE i SET TitleEncrypted = @TitleEncrypted, IsCompleted = @IsCompleted
    FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId;
    SELECT i.NoteItemId, i.NoteId, i.TitleEncrypted, i.Position, i.IsCompleted, i.CreatedAt
    FROM dbo.note_items i
    JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId;
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
