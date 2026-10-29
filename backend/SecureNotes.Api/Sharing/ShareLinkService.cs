using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Sharing;

public sealed class ShareLinkService(
    INoteShareRepository repository,
    IAuditRepository auditRepository,
    IOptions<ShareOptions> options) : IShareLinkService
{
    private const int DefaultExpiryHours = 24;
    private const int MaximumExpiryHours = 24 * 30;
    private readonly ShareOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    public async Task<CreatedNoteShare> CreateAsync(
        Guid userId,
        Guid noteId,
        string permission,
        int expiresInHours,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (permission is not "view" and not "edit")
        {
            throw new ShareLinkRejectedException("Quyền chia sẻ phải là view hoặc edit.");
        }

        if (expiresInHours == 0)
        {
            expiresInHours = DefaultExpiryHours;
        }

        if (expiresInHours is < 1 or > MaximumExpiryHours)
        {
            throw new ShareLinkRejectedException("Thời hạn chia sẻ phải từ 1 giờ đến 30 ngày.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        var shareId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddHours(expiresInHours);
        try
        {
            await repository.CreateAsync(
                userId,
                noteId,
                shareId,
                tokenHash,
                permission,
                expiresAt,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            CryptographicOperations.ZeroMemory(tokenHash);
        }

        await RecordAuditAsync(userId, "share_create", shareId, context, cancellationToken);
        return new CreatedNoteShare(shareId, token, permission, expiresAt);
    }

    public Task<IReadOnlyList<NoteShare>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return repository.ListAsync(userId, noteId, cancellationToken);
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (!await repository.RevokeAsync(userId, noteId, shareId, cancellationToken))
        {
            return false;
        }

        await RecordAuditAsync(userId, "share_revoke", shareId, context, cancellationToken);
        return true;
    }

    public async Task<NoteShareAccess?> GetActiveAsync(string token, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (token.Length != 43 || token.Any(character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
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
            return null;
        }

        if (tokenBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            return null;
        }

        var tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        try
        {
            return await repository.GetActiveAsync(tokenHash, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    public Task RecordAccessAsync(
        NoteShareAccess access,
        HttpContext context,
        CancellationToken cancellationToken) =>
        RecordAuditAsync(access.UserId, "share_access", access.ShareId, context, cancellationToken);

    private async Task RecordAuditAsync(
        Guid userId,
        string action,
        Guid shareId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 512)
        {
            userAgent = userAgent[..512];
        }

        await auditRepository.RecordAsync(
            userId,
            action,
            context.Connection.RemoteIpAddress?.ToString(),
            string.IsNullOrEmpty(userAgent) ? null : userAgent,
            cancellationToken,
            "note_share",
            shareId);
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new ShareLinksDisabledException();
        }
    }
}

public sealed class ShareLinkRejectedException(string message) : Exception(message);

public sealed class ShareLinksDisabledException()
    : Exception("Tính năng chia sẻ ghi chú hiện đang được tắt.");
