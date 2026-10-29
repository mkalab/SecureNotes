using SecureNotes.Api.Models;

namespace SecureNotes.Api.Security;

public interface ITwoFactorService
{
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken);
    Task<TwoFactorEnrollment> BeginEnrollmentAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> EnableAsync(
        Guid userId,
        string code,
        HttpContext context,
        CancellationToken cancellationToken);
    Task<bool> DisableAsync(
        Guid userId,
        string code,
        HttpContext context,
        CancellationToken cancellationToken);
    Task<bool> CreateChallengeAsync(
        Guid userId,
        string authProvider,
        HttpContext context,
        CancellationToken cancellationToken);
    Task<AuthAccount?> CompleteChallengeAsync(
        string code,
        HttpContext context,
        CancellationToken cancellationToken);
}

public sealed record TwoFactorEnrollment(string Secret, string AuthenticatorUri);
public sealed record AuthAccount(Guid UserId, string Email, string Role, bool TwoFactorVerified);
public sealed class TwoFactorEnrollmentException(string message) : Exception(message);
