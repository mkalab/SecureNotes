using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using OtpNet;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Security;

public sealed class TwoFactorService(
    ITwoFactorRepository repository,
    CryptoService crypto,
    AppSessionService sessions,
    IAuditRepository audit,
    IWebHostEnvironment environment) : ITwoFactorService
{
    private const string ChallengeCookieName = "SecureNotes.TwoFactor";
    private const string Issuer = "SecureNotes";

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken) =>
        (await repository.GetDataAsync(userId, cancellationToken))?.Enabled == true;

    public async Task<TwoFactorEnrollment> BeginEnrollmentAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        var current = await repository.GetDataAsync(userId, cancellationToken)
            ?? throw new TwoFactorEnrollmentException("Tài khoản không tồn tại.");
        if (current.Enabled)
        {
            throw new TwoFactorEnrollmentException("Xác minh hai bước đã được bật.");
        }

        var secret = RandomNumberGenerator.GetBytes(20);
        var wrappedKey = await repository.GetWrappedDataKeyAsync(userId, cancellationToken);
        byte[]? dataKey = null;
        byte[]? ciphertext = null;
        try
        {
            dataKey = crypto.UnprotectUserDataKey(wrappedKey);
            ciphertext = crypto.EncryptBytes(secret, dataKey, GetSecretContext(userId));
            if (!await repository.SetPendingSecretAsync(userId, ciphertext, cancellationToken))
            {
                throw new TwoFactorEnrollmentException("Không thể bắt đầu thiết lập xác minh hai bước.");
            }

            var encodedSecret = Base32Encoding.ToString(secret);
            var label = Uri.EscapeDataString($"{Issuer}:{email}");
            var issuer = Uri.EscapeDataString(Issuer);
            var uri = $"otpauth://totp/{label}?secret={encodedSecret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
            return new TwoFactorEnrollment(encodedSecret, uri);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(wrappedKey);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public async Task<IReadOnlyList<string>> EnableAsync(
        Guid userId,
        string code,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var data = await repository.GetDataAsync(userId, cancellationToken)
            ?? throw new TwoFactorEnrollmentException("Tài khoản không tồn tại.");
        if (data.Enabled || data.PendingSecretCiphertext is null)
        {
            throw new TwoFactorEnrollmentException("Hãy bắt đầu thiết lập xác minh hai bước trước.");
        }

        var wrappedKey = await repository.GetWrappedDataKeyAsync(userId, cancellationToken);
        byte[]? dataKey = null;
        byte[]? secret = null;
        byte[]? encryptedSecret = null;
        try
        {
            dataKey = crypto.UnprotectUserDataKey(wrappedKey);
            secret = crypto.DecryptBytes(
                data.PendingSecretCiphertext,
                dataKey,
                GetSecretContext(userId));
            if (!TryVerifyTotp(secret, code, out var step))
            {
                await RecordAuditAsync(userId, "two_factor_enable_failed", context, cancellationToken);
                throw new TwoFactorEnrollmentException("Mã xác minh không hợp lệ hoặc đã được sử dụng.");
            }

            var recoveryCodes = Enumerable.Range(0, 10)
                .Select(_ => FormatRecoveryCode(RandomNumberGenerator.GetBytes(12)))
                .ToArray();
            var recoveryHashes = recoveryCodes.Select(HashRecoveryCode).ToArray();
            encryptedSecret = data.PendingSecretCiphertext;
            await repository.EnableAsync(userId, encryptedSecret, step, recoveryHashes, cancellationToken);
            await RecordAuditAsync(userId, "two_factor_enabled", context, cancellationToken);
            return recoveryCodes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappedKey);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<bool> DisableAsync(
        Guid userId,
        string code,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var data = await repository.GetDataAsync(userId, cancellationToken);
        if (data is not { Enabled: true, SecretCiphertext: not null })
        {
            return false;
        }

        var wrappedKey = await repository.GetWrappedDataKeyAsync(userId, cancellationToken);
        byte[]? dataKey = null;
        byte[]? secret = null;
        try
        {
            dataKey = crypto.UnprotectUserDataKey(wrappedKey);
            secret = crypto.DecryptBytes(data.SecretCiphertext, dataKey, GetSecretContext(userId));
            if (!TryVerifyTotp(secret, code, out var step)
                || !await repository.ConsumeManagementTotpAsync(userId, step, cancellationToken))
            {
                await RecordAuditAsync(userId, "two_factor_disable_failed", context, cancellationToken);
                return false;
            }

            var disabled = await repository.DisableAsync(userId, cancellationToken);
            if (disabled)
            {
                await RecordAuditAsync(userId, "two_factor_disabled", context, cancellationToken);
            }

            return disabled;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappedKey);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<bool> CreateChallengeAsync(
        Guid userId,
        string authProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var hash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        try
        {
            if (!await repository.CreateChallengeAsync(
                userId,
                hash,
                DateTime.UtcNow.AddMinutes(5),
                authProvider,
                cancellationToken))
            {
                return false;
            }

            context.Response.Cookies.Append(ChallengeCookieName, token, CreateChallengeCookieOptions());
            await RecordAuditAsync(
                userId,
                authProvider == "google" ? "google_two_factor_required" : "two_factor_challenge_created",
                context,
                cancellationToken);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public async Task<AuthAccount?> CompleteChallengeAsync(
        string code,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.Cookies.TryGetValue(ChallengeCookieName, out var token))
        {
            return null;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            ClearChallengeCookie(context);
            return null;
        }

        if (tokenBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            ClearChallengeCookie(context);
            return null;
        }

        var challengeHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        byte[]? wrappedKey = null;
        byte[]? dataKey = null;
        byte[]? secret = null;
        try
        {
            var challenge = await repository.GetChallengeAsync(challengeHash, cancellationToken);
            if (challenge is null || challenge.SecretCiphertext is null || challenge.DekCiphertext is null)
            {
                ClearChallengeCookie(context);
                return null;
            }

            var accepted = false;
            if (IsTotpCode(code))
            {
                wrappedKey = challenge.DekCiphertext;
                dataKey = crypto.UnprotectUserDataKey(wrappedKey);
                secret = crypto.DecryptBytes(
                    challenge.SecretCiphertext,
                    dataKey,
                    GetSecretContext(challenge.UserId));
                if (TryVerifyTotp(secret, code, out var step))
                {
                    accepted = await repository.ConsumeTotpChallengeAsync(
                        challengeHash,
                        challenge.UserId,
                        step,
                        cancellationToken);
                }
            }
            else if (TryHashRecoveryCode(code, out var recoveryHash))
            {
                try
                {
                    accepted = await repository.ConsumeRecoveryChallengeAsync(
                        challengeHash,
                        challenge.UserId,
                        recoveryHash,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(recoveryHash);
                }
            }

            if (!accepted)
            {
                await repository.FailChallengeAsync(challengeHash, cancellationToken);
                await RecordAuditAsync(challenge.UserId, "two_factor_login_failed", context, cancellationToken);
                return null;
            }

            ClearChallengeCookie(context);
            await sessions.SignInAsync(
                context,
                challenge.UserId,
                challenge.Email,
                challenge.Role,
                true,
                cancellationToken);
            await RecordAuditAsync(
                challenge.UserId,
                challenge.AuthProvider == "google" ? "google_login" : "login_success",
                context,
                cancellationToken);
            return new AuthAccount(challenge.UserId, challenge.Email, challenge.Role, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeHash);
            if (wrappedKey is not null) CryptographicOperations.ZeroMemory(wrappedKey);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static bool TryVerifyTotp(byte[] secret, string code, out long step)
    {
        step = 0;
        if (!IsTotpCode(code))
        {
            return false;
        }

        return new Totp(secret, step: 30, mode: OtpHashMode.Sha1, totpSize: 6)
            .VerifyTotp(DateTime.UtcNow, code, out step, new VerificationWindow(previous: 1, future: 1));
    }

    private static bool IsTotpCode(string code) =>
        code.Length == 6 && code.All(char.IsAsciiDigit);

    private static string FormatRecoveryCode(byte[] bytes)
    {
        try
        {
            var value = Convert.ToHexString(bytes);
            return $"{value[..8]}-{value[8..16]}-{value[16..]}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryHashRecoveryCode(string code, out byte[] hash)
    {
        hash = [];
        var normalized = code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length != 24 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        hash = SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
        return true;
    }

    private static string HashRecoveryCode(string code)
    {
        _ = TryHashRecoveryCode(code, out var hash);
        try
        {
            return Convert.ToHexString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string GetSecretContext(Guid userId) => $"SecureNotes:totp-secret:v1:{userId:D}";

    private CookieOptions CreateChallengeCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        MaxAge = TimeSpan.FromMinutes(5),
        IsEssential = true
    };

    private void ClearChallengeCookie(HttpContext context) =>
        context.Response.Cookies.Delete(ChallengeCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            IsEssential = true
        });

    private async Task RecordAuditAsync(
        Guid userId,
        string action,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        await audit.RecordAsync(
            userId,
            action,
            context.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            cancellationToken,
            "two_factor");
    }
}
