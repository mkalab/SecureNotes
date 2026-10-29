using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface IAdminRepository
{
    Task<IReadOnlyList<AdminUser>> ListUsersAsync(
        Guid adminUserId,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<bool> SetUserDisabledAsync(
        Guid adminUserId,
        Guid targetUserId,
        bool disabled,
        CancellationToken cancellationToken);
    Task<int> RevokeUserSessionsAsync(Guid adminUserId, Guid targetUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminAuditEntry>> ListAuditAsync(
        Guid adminUserId,
        long? beforeAuditLogId,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminShare>> ListSharesAsync(
        Guid adminUserId,
        int limit,
        CancellationToken cancellationToken);
    Task<bool> RevokeShareAsync(Guid adminUserId, Guid shareId, CancellationToken cancellationToken);
}
