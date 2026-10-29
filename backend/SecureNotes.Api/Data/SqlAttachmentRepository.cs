using System.Data;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public sealed class SqlAttachmentRepository(IConfiguration configuration) : IAttachmentRepository
{
    public async Task<byte[]> GetWrappedDataKeyAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetUserDek");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is byte[] key
            ? key
            : throw new InvalidOperationException("The authenticated account has no wrapped data key.");
    }

    public async Task ReserveAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        string storageId,
        string fileName,
        string contentType,
        long sizeBytes,
        long maxFileBytes,
        int maxFilesPerNote,
        long maxTotalBytesPerUser,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ReserveNoteAttachment");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        command.Parameters.Add(new SqlParameter("@AttachmentId", SqlDbType.UniqueIdentifier) { Value = attachmentId });
        command.Parameters.Add(new SqlParameter("@StorageId", SqlDbType.Char, 64) { Value = storageId });
        command.Parameters.Add(new SqlParameter("@OriginalName", SqlDbType.NVarChar, 255) { Value = fileName });
        command.Parameters.Add(new SqlParameter("@MediaType", SqlDbType.VarChar, 80) { Value = contentType });
        command.Parameters.Add(new SqlParameter("@SizeBytes", SqlDbType.BigInt) { Value = sizeBytes });
        command.Parameters.Add(new SqlParameter("@MaxFileBytes", SqlDbType.BigInt) { Value = maxFileBytes });
        command.Parameters.Add(new SqlParameter("@MaxFilesPerNote", SqlDbType.Int) { Value = maxFilesPerNote });
        command.Parameters.Add(new SqlParameter("@MaxTotalBytes", SqlDbType.BigInt) { Value = maxTotalBytesPerUser });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CompleteAsync(Guid userId, Guid attachmentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.CompleteNoteAttachment");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@AttachmentId", SqlDbType.UniqueIdentifier) { Value = attachmentId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<NoteAttachment>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ListNoteAttachments");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attachments = new List<NoteAttachment>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(new NoteAttachment(
                reader.GetGuid(reader.GetOrdinal("AttachmentId")),
                reader.GetGuid(reader.GetOrdinal("NoteId")),
                reader.GetString(reader.GetOrdinal("OriginalName")),
                reader.GetString(reader.GetOrdinal("MediaType")),
                reader.GetInt64(reader.GetOrdinal("SizeBytes")),
                reader.GetDateTime(reader.GetOrdinal("CreatedAt"))));
        }

        return attachments;
    }

    public async Task<StoredNoteAttachment?> GetAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetNoteAttachment");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        command.Parameters.Add(new SqlParameter("@AttachmentId", SqlDbType.UniqueIdentifier) { Value = attachmentId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStoredAttachment(reader) : null;
    }

    public async Task<string?> DeleteAsync(
        Guid userId,
        Guid noteId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.DeleteNoteAttachment");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        command.Parameters.Add(new SqlParameter("@AttachmentId", SqlDbType.UniqueIdentifier) { Value = attachmentId });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string storageId ? storageId : null;
    }

    public async Task<IReadOnlyList<string>> DeleteExpiredReservationsAsync(
        DateTime createdBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.DeleteExpiredNoteAttachmentReservations");
        command.Parameters.Add(new SqlParameter("@CreatedBefore", SqlDbType.DateTime2) { Value = createdBefore });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var storageIds = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            storageIds.Add(reader.GetString(0));
        }

        return storageIds;
    }

    public async Task<bool> IsStorageIdReferencedAsync(string storageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.IsNoteAttachmentStorageReferenced");
        command.Parameters.Add(new SqlParameter("@StorageId", SqlDbType.Char, 64) { Value = storageId });
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The 'ConnectionStrings:SecureNotes' setting is required for attachment operations.");
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static SqlCommand CreateProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static StoredNoteAttachment ReadStoredAttachment(SqlDataReader reader) =>
        new(
            reader.GetGuid(reader.GetOrdinal("AttachmentId")),
            reader.GetGuid(reader.GetOrdinal("UserId")),
            reader.GetGuid(reader.GetOrdinal("NoteId")),
            reader.GetString(reader.GetOrdinal("StorageId")),
            reader.GetString(reader.GetOrdinal("OriginalName")),
            reader.GetString(reader.GetOrdinal("MediaType")),
            reader.GetInt64(reader.GetOrdinal("SizeBytes")),
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")));
}
