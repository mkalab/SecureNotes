using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface INoteShareRepository
{
    Task CreateAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        byte[] tokenHash,
        string permission,
        DateTime expiresAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteShare>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        CancellationToken cancellationToken);

    Task<NoteShareAccess?> GetActiveAsync(byte[] tokenHash, CancellationToken cancellationToken);
}
