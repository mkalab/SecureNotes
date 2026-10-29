using System.Security.Cryptography;
using System.Text;

namespace SecureNotes.Api.Security;

public sealed class CryptoService : IDisposable
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _keyEncryptionKey;

    public CryptoService(IConfiguration configuration)
    {
        var encodedKey = configuration["SecureNotes:KeyEncryptionKey"];
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException(
                "The 'SecureNotes:KeyEncryptionKey' setting must contain a base64-encoded 32-byte key.");
        }

        try
        {
            _keyEncryptionKey = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The 'SecureNotes:KeyEncryptionKey' setting is not valid base64.", exception);
        }

        if (_keyEncryptionKey.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(_keyEncryptionKey);
            throw new InvalidOperationException(
                "The 'SecureNotes:KeyEncryptionKey' setting must decode to exactly 32 bytes.");
        }
    }

    public byte[] CreateUserDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    public byte[] ProtectUserDataKey(ReadOnlySpan<byte> userDataKey)
    {
        if (userDataKey.Length != KeySize)
        {
            throw new ArgumentException("A user data key must contain exactly 32 bytes.", nameof(userDataKey));
        }

        return Encrypt(userDataKey, _keyEncryptionKey, "SecureNotes:user-data-key:v1"u8);
    }

    public byte[] UnprotectUserDataKey(ReadOnlySpan<byte> protectedUserDataKey)
    {
        var dataKey = Decrypt(protectedUserDataKey, _keyEncryptionKey, "SecureNotes:user-data-key:v1"u8);
        if (dataKey.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new CryptographicException("The protected user data key has an invalid length.");
        }

        return dataKey;
    }

    public byte[] EncryptText(string value, ReadOnlySpan<byte> userDataKey, string context)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Encrypt(plaintext, userDataKey, Encoding.UTF8.GetBytes(context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string DecryptText(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> userDataKey, string context)
    {
        var plaintext = Decrypt(ciphertext, userDataKey, Encoding.UTF8.GetBytes(context));
        try
        {
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public byte[] EncryptBytes(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userDataKey, string context) =>
        Encrypt(plaintext, userDataKey, Encoding.UTF8.GetBytes(context));

    public byte[] DecryptBytes(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> userDataKey, string context) =>
        Decrypt(ciphertext, userDataKey, Encoding.UTF8.GetBytes(context));

    private static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));
        }

        var result = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = result.AsSpan(0, NonceSize);
        var tag = result.AsSpan(NonceSize, TagSize);
        var ciphertext = result.AsSpan(NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return result;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));
        }

        if (encrypted.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The encrypted value is truncated.");
        }

        var plaintext = new byte[encrypted.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            encrypted[..NonceSize],
            encrypted[(NonceSize + TagSize)..],
            encrypted.Slice(NonceSize, TagSize),
            plaintext,
            associatedData);
        return plaintext;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_keyEncryptionKey);
}
