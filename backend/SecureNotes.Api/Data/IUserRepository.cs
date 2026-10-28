using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface IUserRepository
{
    Task<UserCredential> CreateAsync(
        string email,
        byte[]? passwordHash,
        byte[]? passwordSalt,
        int? passwordIterations,
        byte[]? dekCiphertext,
        CancellationToken cancellationToken);

    Task<UserCredential?> GetByEmailAsync(string email, CancellationToken cancellationToken);

    Task<UserCredential?> GetByGoogleSubjectAsync(string subject, CancellationToken cancellationToken);

    Task<UserCredential> CreateGoogleUserAsync(
        string email,
        string subject,
        byte[] dekCiphertext,
        CancellationToken cancellationToken);

    Task<UserCredential> LinkGoogleLoginAsync(
        Guid userId,
        string subject,
        CancellationToken cancellationToken);

    Task RecordFailedLoginAsync(string email, CancellationToken cancellationToken);

    Task ResetFailedLoginAsync(Guid userId, CancellationToken cancellationToken);
}
