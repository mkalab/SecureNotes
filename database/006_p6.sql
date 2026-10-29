:ON ERROR EXIT

USE SecureNotes;
GO

IF OBJECT_ID(N'dbo.note_attachments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.note_attachments
    (
        AttachmentId uniqueidentifier NOT NULL,
        UserId uniqueidentifier NOT NULL,
        NoteId uniqueidentifier NOT NULL,
        StorageId char(64) NOT NULL,
        OriginalName nvarchar(255) NOT NULL,
        MediaType varchar(80) NOT NULL,
        SizeBytes bigint NOT NULL,
        Status tinyint NOT NULL CONSTRAINT DF_note_attachments_Status DEFAULT 0,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_note_attachments_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_note_attachments PRIMARY KEY (AttachmentId),
        CONSTRAINT FK_note_attachments_users FOREIGN KEY (UserId) REFERENCES dbo.users(UserId) ON DELETE CASCADE,
        CONSTRAINT FK_note_attachments_notes FOREIGN KEY (NoteId) REFERENCES dbo.notes(NoteId) ON DELETE CASCADE,
        CONSTRAINT UQ_note_attachments_StorageId UNIQUE (StorageId),
        CONSTRAINT CK_note_attachments_Size CHECK (SizeBytes > 0),
        CONSTRAINT CK_note_attachments_Status CHECK (Status IN (0, 1))
    );
    CREATE INDEX IX_note_attachments_User_Note_Status
        ON dbo.note_attachments(UserId, NoteId, Status);
END;
GO

CREATE OR ALTER PROCEDURE app.ReserveNoteAttachment
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @AttachmentId uniqueidentifier,
    @StorageId char(64),
    @OriginalName nvarchar(255),
    @MediaType varchar(80),
    @SizeBytes bigint,
    @MaxFileBytes bigint,
    @MaxFilesPerNote int,
    @MaxTotalBytes bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @UserExists bit = 0;
    DECLARE @CurrentTotalBytes bigint;
    DECLARE @CurrentNoteFileCount int;

    BEGIN TRANSACTION;

    SELECT @UserExists = 1
    FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
    WHERE UserId = @UserId AND IsDisabled = 0;

    IF @UserExists = 0 OR NOT EXISTS
    (
        SELECT 1 FROM dbo.notes
        WHERE UserId = @UserId AND NoteId = @NoteId
    )
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50040, 'The note was not found.', 1;
    END;

    IF @SizeBytes <= 0 OR @SizeBytes > @MaxFileBytes
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50041, 'The attachment exceeds the per-file limit.', 1;
    END;

    SELECT @CurrentNoteFileCount = COUNT(*)
    FROM dbo.note_attachments
    WHERE UserId = @UserId AND NoteId = @NoteId;

    IF @CurrentNoteFileCount >= @MaxFilesPerNote
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50042, 'The note attachment limit was reached.', 1;
    END;

    SELECT @CurrentTotalBytes = COALESCE(SUM(SizeBytes), 0)
    FROM dbo.note_attachments
    WHERE UserId = @UserId;

    IF @CurrentTotalBytes + @SizeBytes > @MaxTotalBytes
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50043, 'The account attachment quota was reached.', 1;
    END;

    INSERT INTO dbo.note_attachments
        (AttachmentId, UserId, NoteId, StorageId, OriginalName, MediaType, SizeBytes, Status)
    VALUES
        (@AttachmentId, @UserId, @NoteId, @StorageId, @OriginalName, @MediaType, @SizeBytes, 0);

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE app.CompleteNoteAttachment
    @UserId uniqueidentifier,
    @AttachmentId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.note_attachments
    SET Status = 1
    WHERE UserId = @UserId AND AttachmentId = @AttachmentId AND Status = 0;

    SELECT @@ROWCOUNT AS Completed;
END;
GO

CREATE OR ALTER PROCEDURE app.ListNoteAttachments
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AttachmentId, NoteId, OriginalName, MediaType, SizeBytes, CreatedAt
    FROM dbo.note_attachments
    WHERE UserId = @UserId AND NoteId = @NoteId AND Status = 1
    ORDER BY CreatedAt, AttachmentId;
END;
GO

CREATE OR ALTER PROCEDURE app.GetNoteAttachment
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @AttachmentId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT AttachmentId, UserId, NoteId, StorageId, OriginalName, MediaType, SizeBytes, CreatedAt
    FROM dbo.note_attachments
    WHERE UserId = @UserId AND NoteId = @NoteId AND AttachmentId = @AttachmentId AND Status = 1;
END;
GO

CREATE OR ALTER PROCEDURE app.DeleteNoteAttachment
    @UserId uniqueidentifier,
    @NoteId uniqueidentifier,
    @AttachmentId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.note_attachments
    OUTPUT deleted.StorageId
    WHERE UserId = @UserId AND NoteId = @NoteId AND AttachmentId = @AttachmentId;
END;
GO

CREATE OR ALTER PROCEDURE app.DeleteExpiredNoteAttachmentReservations
    @CreatedBefore datetime2(3)
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.note_attachments
    OUTPUT deleted.StorageId
    WHERE Status = 0 AND CreatedAt < @CreatedBefore;
END;
GO

CREATE OR ALTER PROCEDURE app.IsNoteAttachmentStorageReferenced
    @StorageId char(64)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CONVERT(bit, CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.note_attachments WHERE StorageId = @StorageId
    ) THEN 1 ELSE 0 END) AS IsReferenced;
END;
GO

GRANT EXECUTE ON SCHEMA::app TO notes_app;
GO
