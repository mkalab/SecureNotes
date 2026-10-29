using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface IAttachmentRepository
{
    Task<byte[]> GetWrappedDataKeyAsync(Guid userId, CancellationToken cancellationToken);
    Task ReserveAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        string storageId,
        string fileName,
        string contentType,
        long sizeBytes,
        long maxFileBytes,
        int maxFilesPerNote,
        long maxTotalBytesPerUser,
        CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid userId, Guid attachmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NoteAttachment>> ListAsync(Guid userId, Guid noteId, CancellationToken cancellationToken);
    Task<StoredNoteAttachment?> GetAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        CancellationToken cancellationToken);
    Task<string?> DeleteAsync(Guid userId, Guid noteId, Guid attachmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> DeleteExpiredReservationsAsync(DateTime createdBefore, CancellationToken cancellationToken);
    Task<bool> IsStorageIdReferencedAsync(string storageId, CancellationToken cancellationToken);
}
