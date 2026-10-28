namespace SecureNotes.Api.Models;

public sealed record UserCredential(
    Guid UserId,
    string Email,
    string NormalizedEmail,
    byte[]? PasswordHash,
    byte[]? PasswordSalt,
    int? PasswordIterations,
    byte[]? DekCiphertext,
    string Role,
    int FailedCount,
    DateTime? LockedUntil,
    bool IsDisabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);
