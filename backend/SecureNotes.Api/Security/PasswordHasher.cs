using System.Security.Cryptography;

namespace SecureNotes.Api.Security;

public sealed class PasswordHasher
{
    public const int CurrentIterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public (byte[] Hash, byte[] Salt, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            CurrentIterations,
            HashAlgorithmName.SHA256,
            HashSize);
        return (hash, salt, CurrentIterations);
    }

    public bool Verify(string password, byte[]? expectedHash, byte[]? salt, int? iterations)
    {
        if (expectedHash is null
            || expectedHash.Length != HashSize
            || salt is null
            || salt.Length != SaltSize
            || iterations is null
            || iterations is < 1 or > 1_000_000)
        {
            var dummySalt = new byte[SaltSize];
            _ = Rfc2898DeriveBytes.Pbkdf2(password, dummySalt, CurrentIterations, HashAlgorithmName.SHA256, HashSize);
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations.Value,
            HashAlgorithmName.SHA256,
            expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
