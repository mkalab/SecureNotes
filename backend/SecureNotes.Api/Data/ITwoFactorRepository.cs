using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public interface ITwoFactorRepository
{
    Task<byte[]> GetWrappedDataKeyAsync(Guid userId, CancellationToken cancellationToken);
    Task<TwoFactorData?> GetDataAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> SetPendingSecretAsync(Guid userId, byte[] ciphertext, CancellationToken cancellationToken);
    Task EnableAsync(
        Guid userId,
        byte[] ciphertext,
        long lastStep,
        IReadOnlyList<string> recoveryCodeHashes,
        CancellationToken cancellationToken);
    Task<bool> DisableAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> CreateChallengeAsync(
        Guid userId,
        byte[] challengeHash,
        DateTime expiresAtUtc,
        string authProvider,
        CancellationToken cancellationToken);
    Task<TwoFactorChallenge?> GetChallengeAsync(byte[] challengeHash, CancellationToken cancellationToken);
    Task FailChallengeAsync(byte[] challengeHash, CancellationToken cancellationToken);
    Task<bool> ConsumeTotpChallengeAsync(
        byte[] challengeHash,
        Guid userId,
        long step,
        CancellationToken cancellationToken);
    Task<bool> ConsumeManagementTotpAsync(
        Guid userId,
        long step,
        CancellationToken cancellationToken);
    Task<bool> ConsumeRecoveryChallengeAsync(
        byte[] challengeHash,
        Guid userId,
        byte[] codeHash,
        CancellationToken cancellationToken);
}
