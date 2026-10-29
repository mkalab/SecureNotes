:ON ERROR EXIT

USE SecureNotes;
GO

IF OBJECT_ID(N'dbo.share_links', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.share_links
    (
        ShareId uniqueidentifier NOT NULL,
        UserId uniqueidentifier NOT NULL,
        NoteId uniqueidentifier NOT NULL,
        TokenHash binary(32) NOT NULL,
        Permission varchar(4) NOT NULL,
        ExpiresAt datetime2(3) NOT NULL,
        RevokedAt datetime2(3) NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_share_links_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_share_links PRIMARY KEY (ShareId),
        CONSTRAINT FK_share_links_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId),
        CONSTRAINT FK_share_links_notes FOREIGN KEY (NoteId) REFERENCES dbo.notes(NoteId) ON DELETE CASCADE,
        CONSTRAINT UQ_share_links_TokenHash UNIQUE (TokenHash),
        CONSTRAINT CK_share_links_Permission CHECK (Permission IN ('view', 'edit')),
        CONSTRAINT CK_share_links_Expiry CHECK (ExpiresAt > CreatedAt)
    );

    CREATE INDEX IX_share_links_User_Note_Created
        ON dbo.share_links(UserId, NoteId, CreatedAt DESC);
END;
GO

CREATE OR ALTER PROCEDURE app.CreateNoteShare
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @ShareId uniqueidentifier,
    @TokenHash binary(32),
    @Permission varchar(4),
    @ExpiresAt datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Permission NOT IN ('view', 'edit') OR @ExpiresAt <= SYSUTCDATETIME()
        THROW 50071, 'The note share settings are invalid.', 1;

    INSERT INTO dbo.share_links(ShareId, UserId, NoteId, TokenHash, Permission, ExpiresAt)
    SELECT @ShareId, @UserId, @NoteId, @TokenHash, @Permission, @ExpiresAt
    FROM dbo.notes AS n
    INNER JOIN dbo.users AS u ON u.UserId = n.UserId
    WHERE n.NoteId = @NoteId
      AND n.UserId = @UserId
      AND u.IsDisabled = 0;

    IF @@ROWCOUNT = 0
        THROW 50070, 'The note was not found.', 1;
END;
GO

CREATE OR ALTER PROCEDURE app.ListNoteShares
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.ShareId, s.Permission, s.ExpiresAt,
           CONVERT(bit, CASE WHEN s.ExpiresAt <= SYSUTCDATETIME() THEN 1 ELSE 0 END) AS IsExpired,
           s.RevokedAt, s.CreatedAt
    FROM dbo.share_links AS s
    INNER JOIN dbo.notes AS n ON n.NoteId = s.NoteId AND n.UserId = s.UserId
    WHERE s.UserId = @UserId AND s.NoteId = @NoteId
    ORDER BY s.CreatedAt DESC, s.ShareId;
END;
GO

CREATE OR ALTER PROCEDURE app.RevokeNoteShare
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @ShareId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.share_links
    SET RevokedAt = COALESCE(RevokedAt, SYSUTCDATETIME())
    WHERE UserId = @UserId AND NoteId = @NoteId AND ShareId = @ShareId;

    SELECT CONVERT(bit, CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.share_links
        WHERE UserId = @UserId AND NoteId = @NoteId AND ShareId = @ShareId
    ) THEN 1 ELSE 0 END) AS ShareFound;
END;
GO

CREATE OR ALTER PROCEDURE app.GetActiveNoteShare
    @TokenHash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.ShareId, s.UserId, s.NoteId, s.Permission, s.ExpiresAt
    FROM dbo.share_links AS s
    INNER JOIN dbo.notes AS n ON n.NoteId = s.NoteId AND n.UserId = s.UserId
    INNER JOIN dbo.users AS u ON u.UserId = s.UserId AND u.IsDisabled = 0
    WHERE s.TokenHash = @TokenHash
      AND s.RevokedAt IS NULL
      AND s.ExpiresAt > SYSUTCDATETIME();
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
