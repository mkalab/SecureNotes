using Microsoft.AspNetCore.Http;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Attachments;

public interface IAttachmentService
{
    bool Enabled { get; }
    Task<IReadOnlyList<NoteAttachment>> ListAsync(Guid userId, Guid noteId, CancellationToken cancellationToken);
    Task<NoteAttachment> UploadAsync(
        Guid userId,
        Guid noteId,
        IFormFile file,
        HttpContext context,
        CancellationToken cancellationToken);
    Task<DownloadedAttachment?> DownloadAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        HttpContext context,
        CancellationToken cancellationToken);
    Task<bool> DeleteAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        HttpContext context,
        CancellationToken cancellationToken);
    Task DeleteForNoteAsync(Guid userId, Guid noteId, HttpContext context, CancellationToken cancellationToken);
    Task CleanupAsync(CancellationToken cancellationToken);
}

public sealed record DownloadedAttachment(string FileName, string ContentType, byte[] Content);
