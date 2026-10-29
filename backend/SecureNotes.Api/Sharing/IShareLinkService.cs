using Microsoft.AspNetCore.Http;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Sharing;

public interface IShareLinkService
{
    bool Enabled { get; }

    Task<CreatedNoteShare> CreateAsync(
        Guid userId,
        Guid noteId,
        string permission,
        int expiresInHours,
        HttpContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NoteShare>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        HttpContext context,
        CancellationToken cancellationToken);

    Task<NoteShareAccess?> GetActiveAsync(string token, CancellationToken cancellationToken);

    Task RecordAccessAsync(
        NoteShareAccess access,
        HttpContext context,
        CancellationToken cancellationToken);
}
