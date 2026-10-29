namespace SecureNotes.Api.Models;

public sealed record SessionAuthorization(
    Guid UserId,
    string Email,
    string Role,
    bool TwoFactorEnabled,
    bool TwoFactorVerified);
