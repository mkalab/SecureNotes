using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Security;

namespace SecureNotes.Api.Data;

public sealed class P4EncryptionMigrationService(
    IConfiguration configuration,
    CryptoService cryptoService)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var acquireLock = CreateProcedure(connection, "app.AcquireP4EncryptionLock"))
        {
            var result = await acquireLock.ExecuteScalarAsync(cancellationToken);
            if (result is not int lockResult || lockResult < 0)
            {
                throw new InvalidOperationException("Could not acquire the database encryption-migration lock.");
            }
        }

        try
        {
            await MigrateUsersAsync(connection, cancellationToken);
            await using var finalize = CreateProcedure(connection, "app.FinalizeP4Encryption");
            await finalize.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await using var releaseLock = CreateProcedure(connection, "app.ReleaseP4EncryptionLock");
            await releaseLock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task MigrateUsersAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var users = new List<EncryptionUser>();
        await using (var command = CreateProcedure(connection, "app.ListUsersForEncryption"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new EncryptionUser(
                    reader.GetGuid(reader.GetOrdinal("UserId")),
                    reader.IsDBNull(reader.GetOrdinal("DekCiphertext"))
                        ? null
                        : reader.GetFieldValue<byte[]>(reader.GetOrdinal("DekCiphertext"))));
            }
        }

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encryptedDek = user.DekCiphertext ?? await CreateUserDekAsync(connection, user.UserId, cancellationToken);
            var dek = cryptoService.UnprotectUserDataKey(encryptedDek);
            try
            {
                await MigrateNotesAsync(connection, user.UserId, dek, cancellationToken);
                await MigrateChecklistItemsAsync(connection, user.UserId, dek, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    private async Task<byte[]> CreateUserDekAsync(
        SqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var dek = cryptoService.CreateUserDataKey();
        try
        {
            await using var command = CreateProcedure(connection, "app.SetUserDekIfMissing");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@DekCiphertext", SqlDbType.VarBinary, cryptoService.ProtectUserDataKey(dek), -1);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not byte[] storedDek)
            {
                throw new InvalidOperationException($"Could not create a data key for user {userId}.");
            }

            return storedDek;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private async Task MigrateNotesAsync(
        SqlConnection connection,
        Guid userId,
        byte[] dek,
        CancellationToken cancellationToken)
    {
        var notes = new List<LegacyNote>();
        await using (var command = CreateProcedure(connection, "app.GetLegacyNotesForEncryption"))
        {
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                notes.Add(new LegacyNote(
                    reader.GetGuid(reader.GetOrdinal("NoteId")),
                    reader.GetString(reader.GetOrdinal("Title")),
                    reader.GetString(reader.GetOrdinal("Content"))));
            }
        }

        foreach (var note in notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = CreateProcedure(connection, "app.EncryptLegacyNote");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, note.NoteId);
            Add(command, "@TitleEncrypted", SqlDbType.VarBinary,
                cryptoService.EncryptText(note.Title, dek, NoteContext(userId, note.NoteId, "title")), -1);
            Add(command, "@ContentEncrypted", SqlDbType.VarBinary,
                cryptoService.EncryptText(note.Content, dek, NoteContext(userId, note.NoteId, "content")), -1);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task MigrateChecklistItemsAsync(
        SqlConnection connection,
        Guid userId,
        byte[] dek,
        CancellationToken cancellationToken)
    {
        var items = new List<LegacyChecklistItem>();
        await using (var command = CreateProcedure(connection, "app.GetLegacyNoteItemsForEncryption"))
        {
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new LegacyChecklistItem(
                    reader.GetGuid(reader.GetOrdinal("NoteId")),
                    reader.GetGuid(reader.GetOrdinal("NoteItemId")),
                    reader.GetString(reader.GetOrdinal("Title"))));
            }
        }

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = CreateProcedure(connection, "app.EncryptLegacyNoteItem");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, item.NoteId);
            Add(command, "@NoteItemId", SqlDbType.UniqueIdentifier, item.NoteItemId);
            Add(command, "@TitleEncrypted", SqlDbType.VarBinary,
                cryptoService.EncryptText(
                    item.Title,
                    dek,
                    NoteItemContext(userId, item.NoteId, item.NoteItemId)),
                -1);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The 'ConnectionStrings:SecureNotes' setting is required.");
        }

        return new SqlConnection(connectionString);
    }

    private static SqlCommand CreateProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = size == 0 ? new SqlParameter(name, type) : new SqlParameter(name, type, size);
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static string NoteContext(Guid userId, Guid noteId, string field) =>
        $"SecureNotes:note:v1:{userId:D}:{noteId:D}:{field}";

    internal static string NoteItemContext(Guid userId, Guid noteId, Guid itemId) =>
        $"SecureNotes:note-item:v1:{userId:D}:{noteId:D}:{itemId:D}:title";

    private sealed record EncryptionUser(Guid UserId, byte[]? DekCiphertext);
    private sealed record LegacyNote(Guid NoteId, string Title, string Content);
    private sealed record LegacyChecklistItem(Guid NoteId, Guid NoteItemId, string Title);
}
