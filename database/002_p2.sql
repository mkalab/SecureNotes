:ON ERROR EXIT
USE SecureNotes;
GO

IF COL_LENGTH(N'dbo.notes', N'ParentId') IS NULL
    ALTER TABLE dbo.notes ADD ParentId uniqueidentifier NULL;
GO
IF COL_LENGTH(N'dbo.notes', N'Position') IS NULL
    ALTER TABLE dbo.notes ADD Position int NOT NULL CONSTRAINT DF_notes_Position DEFAULT 0;
GO
IF COL_LENGTH(N'dbo.notes', N'Title') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.notes)
        THROW 50020, 'Legacy note content needs an explicit encryption-aware migration.', 1;
    ALTER TABLE dbo.notes ADD Title nvarchar(200) NULL;
END;
GO
IF COL_LENGTH(N'dbo.notes', N'TitleCiphertext') IS NOT NULL
BEGIN
    ALTER TABLE dbo.notes ALTER COLUMN Title nvarchar(200) NOT NULL;
    ALTER TABLE dbo.notes DROP COLUMN TitleCiphertext;
END;
GO
IF COL_LENGTH(N'dbo.notes', N'Content') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.notes)
        THROW 50020, 'Legacy note content needs an explicit encryption-aware migration.', 1;
    ALTER TABLE dbo.notes ADD Content nvarchar(max) NULL;
END;
GO
IF COL_LENGTH(N'dbo.notes', N'ContentCiphertext') IS NOT NULL
BEGIN
    ALTER TABLE dbo.notes ALTER COLUMN Content nvarchar(max) NOT NULL;
    ALTER TABLE dbo.notes DROP COLUMN ContentCiphertext;
END;
GO
IF COL_LENGTH(N'dbo.note_items', N'Title') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.note_items)
        THROW 50020, 'Legacy checklist content needs an explicit encryption-aware migration.', 1;
    ALTER TABLE dbo.note_items ADD Title nvarchar(1000) NULL;
END;
GO
IF COL_LENGTH(N'dbo.note_items', N'TitleCiphertext') IS NOT NULL
BEGIN
    ALTER TABLE dbo.note_items ALTER COLUMN Title nvarchar(1000) NOT NULL;
    ALTER TABLE dbo.note_items DROP COLUMN TitleCiphertext;
END;
GO
IF OBJECT_ID(N'dbo.FK_notes_parent', N'F') IS NULL
    ALTER TABLE dbo.notes ADD CONSTRAINT FK_notes_parent FOREIGN KEY (ParentId) REFERENCES dbo.notes(NoteId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.notes') AND name = N'IX_notes_User_Position')
    CREATE INDEX IX_notes_User_Position ON dbo.notes(UserId, Position, CreatedAt DESC);
GO

CREATE OR ALTER PROCEDURE app.ListNotes
    @UserId uniqueidentifier,
    @Search nvarchar(200) = NULL,
    @CategoryId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Pattern nvarchar(402) = NULL;
    IF NULLIF(LTRIM(RTRIM(@Search)), N'') IS NOT NULL
    BEGIN
        DECLARE @Escaped nvarchar(200) = LTRIM(RTRIM(@Search));
        SET @Escaped = REPLACE(REPLACE(REPLACE(@Escaped, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');
        SET @Pattern = N'%' + @Escaped + N'%';
    END;

    SELECT n.NoteId, n.UserId, n.CategoryId, c.Name AS CategoryName, n.ParentId,
           n.Title, n.Content, n.SourceUrl, n.Color, n.Importance, n.IsPinned,
           n.Position, n.CreatedAt, n.UpdatedAt,
           (SELECT COUNT(*) FROM dbo.note_items i WHERE i.NoteId = n.NoteId) AS ItemCount
    FROM dbo.notes n
    LEFT JOIN dbo.categories c ON c.CategoryId = n.CategoryId AND c.UserId = n.UserId
    WHERE n.UserId = @UserId
      AND (@CategoryId IS NULL OR n.CategoryId = @CategoryId)
      AND (@Pattern IS NULL OR n.Title LIKE @Pattern OR n.Content LIKE @Pattern)
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
           n.Title, n.Content, n.SourceUrl, n.Color, n.Importance, n.IsPinned,
           n.Position, n.CreatedAt, n.UpdatedAt,
           (SELECT COUNT(*) FROM dbo.note_items i WHERE i.NoteId = n.NoteId) AS ItemCount
    FROM dbo.notes n
    LEFT JOIN dbo.categories c ON c.CategoryId = n.CategoryId AND c.UserId = n.UserId
    WHERE n.UserId = @UserId AND n.NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateNote
    @UserId uniqueidentifier,
    @Title nvarchar(200),
    @Content nvarchar(max),
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
    IF NULLIF(LTRIM(RTRIM(@Title)), N'') IS NULL
        THROW 50012, 'Note title is required.', 1;

    DECLARE @NoteId uniqueidentifier = NEWID();
    INSERT INTO dbo.notes (NoteId, UserId, CategoryId, ParentId, Title, Content, SourceUrl, Color, Importance, IsPinned, Position)
    VALUES (@NoteId, @UserId, @CategoryId, @ParentId, @Title, @Content, @SourceUrl, @Color, @Importance, @IsPinned, @Position);
    EXEC app.GetNote @UserId = @UserId, @NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.UpdateNote
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @Title nvarchar(200),
    @Content nvarchar(max),
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
    IF NULLIF(LTRIM(RTRIM(@Title)), N'') IS NULL
        THROW 50012, 'Note title is required.', 1;

    UPDATE dbo.notes
    SET Title = @Title, Content = @Content, CategoryId = @CategoryId, ParentId = @ParentId,
        SourceUrl = @SourceUrl, Color = @Color, Importance = @Importance,
        IsPinned = @IsPinned, Position = @Position, UpdatedAt = SYSUTCDATETIME()
    WHERE NoteId = @NoteId AND UserId = @UserId;
    IF @@ROWCOUNT = 0 RETURN;
    EXEC app.GetNote @UserId = @UserId, @NoteId = @NoteId;
END;
GO

CREATE OR ALTER PROCEDURE app.DeleteNote
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.notes SET ParentId = NULL, UpdatedAt = SYSUTCDATETIME()
    WHERE ParentId = @NoteId AND UserId = @UserId;
    DELETE FROM dbo.notes WHERE NoteId = @NoteId AND UserId = @UserId;
    SELECT @@ROWCOUNT AS RowsAffected;
END;
GO

CREATE OR ALTER PROCEDURE app.ListCategories
    @UserId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.CategoryId, c.Name, c.CreatedAt, COUNT(n.NoteId) AS NoteCount
    FROM dbo.categories c
    LEFT JOIN dbo.notes n ON n.CategoryId = c.CategoryId AND n.UserId = c.UserId
    WHERE c.UserId = @UserId
    GROUP BY c.CategoryId, c.Name, c.CreatedAt
    ORDER BY c.Name;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateCategory
    @UserId uniqueidentifier,
    @Name nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@Name)), N'') IS NULL
        THROW 50012, 'Category name is required.', 1;
    DECLARE @Created TABLE (CategoryId uniqueidentifier, Name nvarchar(100), CreatedAt datetime2(3));
    INSERT INTO dbo.categories (UserId, Name, NormalizedName)
    OUTPUT inserted.CategoryId, inserted.Name, inserted.CreatedAt INTO @Created
    VALUES (@UserId, LTRIM(RTRIM(@Name)), LOWER(LTRIM(RTRIM(@Name))));
    SELECT CategoryId, Name, CreatedAt FROM @Created;
END;
GO

CREATE OR ALTER PROCEDURE app.UpdateCategory
    @UserId uniqueidentifier,
    @CategoryId uniqueidentifier,
    @Name nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@Name)), N'') IS NULL
        THROW 50012, 'Category name is required.', 1;
    UPDATE dbo.categories
    SET Name = LTRIM(RTRIM(@Name)), NormalizedName = LOWER(LTRIM(RTRIM(@Name)))
    WHERE CategoryId = @CategoryId AND UserId = @UserId;
    SELECT CategoryId, Name, CreatedAt FROM dbo.categories WHERE CategoryId = @CategoryId AND UserId = @UserId;
END;
GO

CREATE OR ALTER PROCEDURE app.DeleteCategory
    @UserId uniqueidentifier,
    @CategoryId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.notes SET CategoryId = NULL, UpdatedAt = SYSUTCDATETIME()
    WHERE UserId = @UserId AND CategoryId = @CategoryId;
    DELETE FROM dbo.categories WHERE CategoryId = @CategoryId AND UserId = @UserId;
    SELECT @@ROWCOUNT AS RowsAffected;
END;
GO

CREATE OR ALTER PROCEDURE app.ListNoteItems
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT i.NoteItemId, i.NoteId, i.Title, i.Position, i.IsCompleted, i.CreatedAt
    FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId
    ORDER BY i.Position, i.CreatedAt;
END;
GO

CREATE OR ALTER PROCEDURE app.CreateNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @Title nvarchar(1000)
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@Title)), N'') IS NULL
        THROW 50012, 'Checklist item title is required.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.notes WHERE NoteId = @NoteId AND UserId = @UserId)
        THROW 50011, 'Parent note not found.', 1;
    DECLARE @Created TABLE
    (
        NoteItemId uniqueidentifier,
        NoteId uniqueidentifier,
        Title nvarchar(1000),
        Position int,
        IsCompleted bit,
        CreatedAt datetime2(3)
    );
    BEGIN TRANSACTION;
    DECLARE @Position int;
    SELECT @Position = ISNULL(MAX(Position), -1) + 1
    FROM dbo.note_items WITH (UPDLOCK, HOLDLOCK)
    WHERE NoteId = @NoteId;
    INSERT INTO dbo.note_items (NoteId, Title, Position)
    OUTPUT inserted.NoteItemId, inserted.NoteId, inserted.Title, inserted.Position,
           inserted.IsCompleted, inserted.CreatedAt INTO @Created
    VALUES (@NoteId, @Title, @Position);
    COMMIT TRANSACTION;
    SELECT NoteItemId, NoteId, Title, Position, IsCompleted, CreatedAt FROM @Created;
END;
GO

CREATE OR ALTER PROCEDURE app.UpdateNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @NoteItemId uniqueidentifier,
    @Title nvarchar(1000),
    @IsCompleted bit
AS
BEGIN
    SET NOCOUNT ON;
    IF NULLIF(LTRIM(RTRIM(@Title)), N'') IS NULL
        THROW 50012, 'Checklist item title is required.', 1;
    UPDATE i SET Title = @Title, IsCompleted = @IsCompleted
    FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId;
    SELECT i.NoteItemId, i.NoteId, i.Title, i.Position, i.IsCompleted, i.CreatedAt
    FROM dbo.note_items i
    JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId;
END;
GO

CREATE OR ALTER PROCEDURE app.DeleteNoteItem
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @NoteItemId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    DELETE i FROM dbo.note_items i JOIN dbo.notes n ON n.NoteId = i.NoteId
    WHERE n.UserId = @UserId AND i.NoteId = @NoteId AND i.NoteItemId = @NoteItemId;
    SELECT @@ROWCOUNT AS RowsAffected;
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
