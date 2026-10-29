namespace SecureNotes.Api.Models;

public sealed record NoteShare(
    Guid ShareId,
    string Permission,
    DateTime ExpiresAt,
    bool IsExpired,
    DateTime? RevokedAt,
    DateTime CreatedAt);

public sealed record NoteShareAccess(
    Guid ShareId,
    Guid UserId,
    Guid NoteId,
    string Permission,
    DateTime ExpiresAt);

public sealed record SharedNoteResponse(string Title, string Content, string Permission, DateTime ExpiresAt);

public sealed record CreatedNoteShare(Guid ShareId, string Token, string Permission, DateTime ExpiresAt);

public sealed record SharedNoteUpdateRequest(string Title, string Content);
