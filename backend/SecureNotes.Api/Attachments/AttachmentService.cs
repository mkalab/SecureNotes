using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SecureNotes.Api.Data;
using SecureNotes.Api.Models;
using SecureNotes.Api.Security;

namespace SecureNotes.Api.Attachments;

public sealed class AttachmentService(
    IAttachmentRepository repository,
    AttachmentContentValidator validator,
    CryptoService cryptoService,
    IAuditRepository auditRepository,
    IOptions<AttachmentOptions> options,
    IWebHostEnvironment environment,
    ILogger<AttachmentService> logger) : IAttachmentService
{
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromDays(1);
    private readonly AttachmentOptions _options = options.Value;
    private readonly string _storagePath = GetStoragePath(options.Value.StoragePath, environment);

    public bool Enabled => _options.Enabled;

    public Task<IReadOnlyList<NoteAttachment>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        return repository.ListAsync(userId, noteId, cancellationToken);
    }

    public async Task<NoteAttachment> UploadAsync(
        Guid userId,
        Guid noteId,
        IFormFile file,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (file.Length <= 0)
        {
            throw new AttachmentRejectedException("Tệp rỗng không được hỗ trợ.");
        }

        if (file.Length > _options.MaxFileBytes)
        {
            throw new AttachmentLimitExceededException(
                $"Mỗi tệp được giới hạn tối đa {_options.MaxFileBytes / (1024 * 1024)} MiB.");
        }

        var originalContent = new byte[checked((int)file.Length)];
        await using (var input = file.OpenReadStream())
        {
            await input.ReadExactlyAsync(originalContent, cancellationToken);
        }
        ValidatedAttachment? validated = null;
        byte[]? wrappedKey = null;
        byte[]? dataKey = null;
        byte[]? encryptedContent = null;
        var attachmentId = Guid.NewGuid();
        var storageId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var temporaryPath = Path.Combine(_storagePath, storageId + ".tmp");
        var finalPath = Path.Combine(_storagePath, storageId + ".blob");
        var reserved = false;
        var completed = false;

        try
        {
            validated = await validator.ValidateAsync(
                file.FileName,
                file.ContentType,
                originalContent,
                cancellationToken);
            if (validated.Content.Length > _options.MaxFileBytes)
            {
                throw new AttachmentLimitExceededException(
                    $"Tệp sau khi xử lý vượt giới hạn {_options.MaxFileBytes / (1024 * 1024)} MiB.");
            }

            await repository.ReserveAsync(
                userId,
                noteId,
                attachmentId,
                storageId,
                validated.FileName,
                validated.ContentType,
                validated.Content.Length,
                _options.MaxFileBytes,
                _options.MaxFilesPerNote,
                _options.MaxTotalBytesPerUser,
                cancellationToken);
            reserved = true;

            wrappedKey = await repository.GetWrappedDataKeyAsync(userId, cancellationToken);
            dataKey = cryptoService.UnprotectUserDataKey(wrappedKey);
            encryptedContent = cryptoService.EncryptBytes(
                validated.Content,
                dataKey,
                GetEncryptionContext(userId, noteId, attachmentId));

            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(encryptedContent, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, finalPath);
            if (!await repository.CompleteAsync(userId, attachmentId, cancellationToken))
            {
                throw new InvalidOperationException("The attachment reservation could not be completed.");
            }

            completed = true;
            await RecordAuditAsync(userId, "attachment_upload", attachmentId, context, cancellationToken);
            return new NoteAttachment(
                attachmentId,
                noteId,
                validated.FileName,
                validated.ContentType,
                validated.Content.Length,
                DateTime.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalContent);
            if (validated is not null && !ReferenceEquals(validated.Content, originalContent))
            {
                CryptographicOperations.ZeroMemory(validated.Content);
            }

            ZeroIfPresent(wrappedKey);
            ZeroIfPresent(dataKey);
            ZeroIfPresent(encryptedContent);

            if (!completed)
            {
                if (reserved)
                {
                    try
                    {
                        await repository.DeleteAsync(userId, noteId, attachmentId, CancellationToken.None);
                    }
                    catch (SqlException exception)
                    {
                        logger.LogError(exception, "Could not remove a failed attachment reservation {AttachmentId}.", attachmentId);
                    }
                }

                DeleteFileIfPresent(temporaryPath);
                DeleteFileIfPresent(finalPath);
            }
        }
    }

    public async Task<DownloadedAttachment?> DownloadAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var attachment = await repository.GetAsync(userId, noteId, attachmentId, cancellationToken);
        if (attachment is null)
        {
            return null;
        }

        var filePath = GetBlobPath(attachment.StorageId);
        var encryptedContent = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var wrappedKey = await repository.GetWrappedDataKeyAsync(userId, cancellationToken);
        byte[]? dataKey = null;
        byte[]? content = null;
        var handedOff = false;
        try
        {
            dataKey = cryptoService.UnprotectUserDataKey(wrappedKey);
            content = cryptoService.DecryptBytes(
                encryptedContent,
                dataKey,
                GetEncryptionContext(userId, noteId, attachmentId));
            if (content.LongLength != attachment.SizeBytes)
            {
                throw new CryptographicException("The decrypted attachment length does not match its metadata.");
            }

            await RecordAuditAsync(userId, "attachment_download", attachmentId, context, cancellationToken);
            handedOff = true;
            return new DownloadedAttachment(attachment.FileName, attachment.ContentType, content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedContent);
            ZeroIfPresent(wrappedKey);
            ZeroIfPresent(dataKey);
            if (!handedOff)
            {
                ZeroIfPresent(content);
            }
        }
    }

    public async Task<bool> DeleteAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var storageId = await repository.DeleteAsync(userId, noteId, attachmentId, cancellationToken);
        if (storageId is null)
        {
            return false;
        }

        DeleteFileIfPresent(GetBlobPath(storageId));
        DeleteFileIfPresent(GetTempPath(storageId));
        await RecordAuditAsync(userId, "attachment_delete", attachmentId, context, cancellationToken);
        return true;
    }

    public async Task DeleteForNoteAsync(
        Guid userId,
        Guid noteId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var attachments = await repository.ListAsync(userId, noteId, cancellationToken);
        foreach (var attachment in attachments)
        {
            await DeleteAsync(userId, noteId, attachment.AttachmentId, context, cancellationToken);
        }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(OrphanGracePeriod);
        var expiredStorageIds = await repository.DeleteExpiredReservationsAsync(cutoff, cancellationToken);
        foreach (var storageId in expiredStorageIds)
        {
            DeleteFileIfPresent(GetBlobPath(storageId));
            DeleteFileIfPresent(GetTempPath(storageId));
        }

        foreach (var path in Directory.EnumerateFiles(_storagePath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                DeleteFileIfPresent(path);
                continue;
            }

            if (!fileName.EndsWith(".blob", StringComparison.Ordinal))
            {
                continue;
            }

            var storageId = fileName[..^5];
            if (!IsValidStorageId(storageId))
            {
                DeleteFileIfPresent(path);
                continue;
            }

            if (!await repository.IsStorageIdReferencedAsync(storageId, cancellationToken))
            {
                DeleteFileIfPresent(path);
            }
        }
    }

    private async Task RecordAuditAsync(
        Guid userId,
        string action,
        Guid attachmentId,
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
            "attachment",
            attachmentId);
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new AttachmentFeatureDisabledException();
        }
    }

    private string GetBlobPath(string storageId) =>
        Path.Combine(_storagePath, ValidateStorageId(storageId) + ".blob");

    private string GetTempPath(string storageId) =>
        Path.Combine(_storagePath, ValidateStorageId(storageId) + ".tmp");

    private static string GetEncryptionContext(Guid userId, Guid noteId, Guid attachmentId) =>
        $"SecureNotes:attachment:v1:{userId:N}:{noteId:N}:{attachmentId:N}";

    private static string ValidateStorageId(string storageId) =>
        IsValidStorageId(storageId)
            ? storageId
            : throw new InvalidOperationException("The attachment storage identifier is invalid.");

    private static bool IsValidStorageId(string storageId) =>
        storageId.Length == 64 && storageId.All(Uri.IsHexDigit);

    private static string GetStoragePath(string configuredPath, IWebHostEnvironment environment)
    {
        var storagePath = Path.GetFullPath(configuredPath);
        var webRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot"));
        var relativePath = Path.GetRelativePath(webRoot, storagePath);
        if (relativePath == "." || !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attachment storage must be outside the web root.");
        }

        Directory.CreateDirectory(storagePath);
        return storagePath;
    }

    private void DeleteFileIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Could not remove attachment storage file {StoragePath}.", path);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogError(exception, "Could not remove attachment storage file {StoragePath}.", path);
            throw;
        }
    }

    private static void ZeroIfPresent(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

public sealed class AttachmentFeatureDisabledException()
    : Exception("Tính năng tệp đính kèm hiện đang được tắt.");

public sealed class AttachmentLimitExceededException(string message)
    : Exception(message);
