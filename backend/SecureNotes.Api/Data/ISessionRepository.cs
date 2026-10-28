namespace SecureNotes.Api.Data;

public interface ISessionRepository
{
    Task CreateAsync(Guid userId, byte[] sessionIdHash, DateTime expiresAtUtc, CancellationToken cancellationToken);
    Task<bool> ValidateAndTouchAsync(
        byte[] sessionIdHash,
        DateTime nowUtc,
        int idleTimeoutMinutes,
        CancellationToken cancellationToken);
    Task<bool> RecordSecurityViolationAsync(
        byte[] sessionIdHash,
        DateTime nowUtc,
        string action,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
    Task RevokeAsync(byte[] sessionIdHash, CancellationToken cancellationToken);
}
