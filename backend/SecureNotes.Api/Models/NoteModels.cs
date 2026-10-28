namespace SecureNotes.Api.Models;

public sealed record Note(
    Guid NoteId,
    Guid UserId,
    Guid? CategoryId,
    string? CategoryName,
    Guid? ParentId,
    string Title,
    string Content,
    string? SourceUrl,
    string Color,
    byte Importance,
    bool IsPinned,
    int Position,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int ItemCount);

public sealed record NoteItem(
    Guid NoteItemId,
    Guid NoteId,
    string Title,
    int Position,
    bool IsCompleted,
    DateTime CreatedAt);

public sealed record NoteCategory(Guid CategoryId, string Name, DateTime CreatedAt, int NoteCount);
