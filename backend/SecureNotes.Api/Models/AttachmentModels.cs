namespace SecureNotes.Api.Models;

public sealed record NoteAttachment(
    Guid AttachmentId,
    Guid NoteId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime CreatedAt);

public sealed record StoredNoteAttachment(
    Guid AttachmentId,
    Guid UserId,
    Guid NoteId,
    string StorageId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime CreatedAt);
