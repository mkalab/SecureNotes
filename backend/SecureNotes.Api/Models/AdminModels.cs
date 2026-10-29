namespace SecureNotes.Api.Models;

public sealed record AdminUser(
    Guid UserId,
    string Email,
    string Role,
    bool IsDisabled,
    int FailedCount,
    DateTime? LockedUntil,
    bool TwoFactorEnabled,
    DateTime CreatedAt);

public sealed record AdminAuditEntry(
    long AuditLogId,
    Guid? UserId,
    string? Email,
    string Action,
    string? ResourceType,
    Guid? ResourceId,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAt);

public sealed record AdminShare(
    Guid ShareId,
    Guid NoteId,
    Guid UserId,
    string Email,
    string Permission,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    DateTime CreatedAt);

public sealed record TwoFactorData(
    bool Enabled,
    byte[]? SecretCiphertext,
    byte[]? PendingSecretCiphertext,
    long? LastStep);

public sealed record TwoFactorChallenge(
    Guid UserId,
    string Email,
    string Role,
    string AuthProvider,
    byte[]? SecretCiphertext,
    long? LastStep,
    byte[]? DekCiphertext);
