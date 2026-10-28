using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;
using SecureNotes.Api.Security;

namespace SecureNotes.Api.Data;

public sealed class SqlNotesRepository(IConfiguration configuration, CryptoService cryptoService) : INotesRepository
{
    public async Task<IReadOnlyList<Note>> ListNotesAsync(
        Guid userId,
        string? search,
        Guid? categoryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, userId, cancellationToken);
        try
        {
            await using var command = CreateProcedure(connection, "app.ListNotes");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@CategoryId", SqlDbType.UniqueIdentifier, (object?)categoryId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<Note>();
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(ReadNote(reader, dek, cryptoService));
            }

            if (string.IsNullOrWhiteSpace(search))
            {
                return result;
            }

            return result.Where(note =>
                note.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || note.Content.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<Note?> GetNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, userId, cancellationToken);
        try
        {
            await using var command = CreateProcedure(connection, "app.GetNote");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, noteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadNote(reader, dek, cryptoService) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<Note?> CreateNoteAsync(NoteWriteModel note, CancellationToken cancellationToken)
    {
        if (note.NoteId is null)
        {
            throw new ArgumentException("An ID is required to create a note.", nameof(note));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, note.UserId, cancellationToken);
        try
        {
            await using var command = CreateNoteProcedure(connection, "app.CreateNote", note, dek);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadNote(reader, dek, cryptoService) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<Note?> UpdateNoteAsync(NoteWriteModel note, CancellationToken cancellationToken)
    {
        if (note.NoteId is null)
        {
            throw new ArgumentException("An ID is required to update a note.", nameof(note));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, note.UserId, cancellationToken);
        try
        {
            await using var command = CreateNoteProcedure(connection, "app.UpdateNote", note, dek);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadNote(reader, dek, cryptoService) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public Task<bool> DeleteNoteAsync(Guid userId, Guid noteId, CancellationToken cancellationToken) =>
        ExecuteDeleteAsync("app.DeleteNote", userId, noteId, null, cancellationToken);

    public async Task<IReadOnlyList<NoteCategory>> ListCategoriesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ListCategories");
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<NoteCategory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCategory(reader));
        }

        return result;
    }

    public async Task<NoteCategory?> CreateCategoryAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.CreateCategory");
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
        Add(command, "@Name", SqlDbType.NVarChar, name, 100);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCategory(reader) : null;
    }

    public async Task<NoteCategory?> UpdateCategoryAsync(
        Guid userId,
        Guid categoryId,
        string name,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.UpdateCategory");
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
        Add(command, "@CategoryId", SqlDbType.UniqueIdentifier, categoryId);
        Add(command, "@Name", SqlDbType.NVarChar, name, 100);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCategory(reader) : null;
    }

    public Task<bool> DeleteCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken) =>
        ExecuteDeleteAsync("app.DeleteCategory", userId, categoryId, null, cancellationToken);

    public async Task<IReadOnlyList<NoteItem>> ListItemsAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, userId, cancellationToken);
        try
        {
            await using var command = CreateProcedure(connection, "app.ListNoteItems");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, noteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<NoteItem>();
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(ReadItem(reader, dek, cryptoService, userId));
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<NoteItem?> CreateItemAsync(
        Guid userId,
        Guid noteId,
        Guid noteItemId,
        string title,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, userId, cancellationToken);
        try
        {
            await using var command = CreateProcedure(connection, "app.CreateNoteItem");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, noteId);
            Add(command, "@NoteItemId", SqlDbType.UniqueIdentifier, noteItemId);
            Add(command, "@TitleEncrypted", SqlDbType.VarBinary,
                cryptoService.EncryptText(title, dek, P4EncryptionMigrationService.NoteItemContext(userId, noteId, noteItemId)), -1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader, dek, cryptoService, userId) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<NoteItem?> UpdateItemAsync(
        Guid userId,
        Guid noteId,
        Guid itemId,
        string title,
        bool isCompleted,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var dek = await GetUserDataKeyAsync(connection, userId, cancellationToken);
        try
        {
            await using var command = CreateProcedure(connection, "app.UpdateNoteItem");
            Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
            Add(command, "@NoteId", SqlDbType.UniqueIdentifier, noteId);
            Add(command, "@NoteItemId", SqlDbType.UniqueIdentifier, itemId);
            Add(command, "@TitleEncrypted", SqlDbType.VarBinary,
                cryptoService.EncryptText(title, dek, P4EncryptionMigrationService.NoteItemContext(userId, noteId, itemId)), -1);
            Add(command, "@IsCompleted", SqlDbType.Bit, isCompleted);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadItem(reader, dek, cryptoService, userId) : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public Task<bool> DeleteItemAsync(
        Guid userId,
        Guid noteId,
        Guid itemId,
        CancellationToken cancellationToken) =>
        ExecuteDeleteAsync("app.DeleteNoteItem", userId, noteId, itemId, cancellationToken);

    private async Task<bool> ExecuteDeleteAsync(
        string procedure,
        Guid userId,
        Guid id,
        Guid? childId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, procedure);
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
        Add(command, procedure == "app.DeleteNoteItem" ? "@NoteId" : procedure == "app.DeleteNote" ? "@NoteId" : "@CategoryId",
            SqlDbType.UniqueIdentifier, id);
        if (childId is not null)
        {
            Add(command, "@NoteItemId", SqlDbType.UniqueIdentifier, childId.Value);
        }

        var affected = await command.ExecuteScalarAsync(cancellationToken);
        return affected is int count && count > 0;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The 'ConnectionStrings:SecureNotes' setting is required.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static SqlCommand CreateProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private SqlCommand CreateNoteProcedure(SqlConnection connection, string name, NoteWriteModel note, byte[] dek)
    {
        if (note.NoteId is null)
        {
            throw new ArgumentException("An ID is required to save a note.", nameof(note));
        }

        var command = CreateProcedure(connection, name);
        Add(command, "@NoteId", SqlDbType.UniqueIdentifier, note.NoteId.Value);
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, note.UserId);
        Add(command, "@TitleEncrypted", SqlDbType.VarBinary,
            cryptoService.EncryptText(
                note.Title,
                dek,
                P4EncryptionMigrationService.NoteContext(note.UserId, note.NoteId.Value, "title")),
            -1);
        Add(command, "@ContentEncrypted", SqlDbType.VarBinary,
            cryptoService.EncryptText(
                note.Content,
                dek,
                P4EncryptionMigrationService.NoteContext(note.UserId, note.NoteId.Value, "content")),
            -1);
        Add(command, "@CategoryId", SqlDbType.UniqueIdentifier, (object?)note.CategoryId ?? DBNull.Value);
        Add(command, "@ParentId", SqlDbType.UniqueIdentifier, (object?)note.ParentId ?? DBNull.Value);
        Add(command, "@SourceUrl", SqlDbType.NVarChar, (object?)note.SourceUrl ?? DBNull.Value, 2048);
        Add(command, "@Color", SqlDbType.Char, note.Color, 7);
        Add(command, "@Importance", SqlDbType.TinyInt, note.Importance);
        Add(command, "@IsPinned", SqlDbType.Bit, note.IsPinned);
        Add(command, "@Position", SqlDbType.Int, note.Position);
        return command;
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, object value, int size = 0)
    {
        var parameter = size == 0
            ? new SqlParameter(name, type)
            : new SqlParameter(name, type, size);
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Note ReadNote(SqlDataReader reader, byte[] dek, CryptoService cryptoService)
    {
        var userId = reader.GetGuid(reader.GetOrdinal("UserId"));
        var noteId = reader.GetGuid(reader.GetOrdinal("NoteId"));
        return new Note(
        noteId,
        userId,
        GetNullableGuid(reader, "CategoryId"),
        GetNullableString(reader, "CategoryName"),
        GetNullableGuid(reader, "ParentId"),
        cryptoService.DecryptText(
            reader.GetFieldValue<byte[]>(reader.GetOrdinal("TitleEncrypted")),
            dek,
            P4EncryptionMigrationService.NoteContext(userId, noteId, "title")),
        cryptoService.DecryptText(
            reader.GetFieldValue<byte[]>(reader.GetOrdinal("ContentEncrypted")),
            dek,
            P4EncryptionMigrationService.NoteContext(userId, noteId, "content")),
        GetNullableString(reader, "SourceUrl"),
        reader.GetString(reader.GetOrdinal("Color")),
        reader.GetByte(reader.GetOrdinal("Importance")),
        reader.GetBoolean(reader.GetOrdinal("IsPinned")),
        reader.GetInt32(reader.GetOrdinal("Position")),
        reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
        reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
        reader.GetInt32(reader.GetOrdinal("ItemCount")));
    }

    private static NoteCategory ReadCategory(SqlDataReader reader)
    {
        var noteCount = -1;
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (reader.GetName(index) == "NoteCount")
            {
                noteCount = index;
                break;
            }
        }
        return new NoteCategory(
            reader.GetGuid(reader.GetOrdinal("CategoryId")),
            reader.GetString(reader.GetOrdinal("Name")),
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            noteCount < 0 || reader.IsDBNull(noteCount) ? 0 : reader.GetInt32(noteCount));
    }

    private static NoteItem ReadItem(SqlDataReader reader, byte[] dek, CryptoService cryptoService, Guid userId)
    {
        var noteId = reader.GetGuid(reader.GetOrdinal("NoteId"));
        var noteItemId = reader.GetGuid(reader.GetOrdinal("NoteItemId"));
        return new NoteItem(
            noteItemId,
            noteId,
            cryptoService.DecryptText(
                reader.GetFieldValue<byte[]>(reader.GetOrdinal("TitleEncrypted")),
                dek,
                P4EncryptionMigrationService.NoteItemContext(userId, noteId, noteItemId)),
            reader.GetInt32(reader.GetOrdinal("Position")),
            reader.GetBoolean(reader.GetOrdinal("IsCompleted")),
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")));
    }

    private async Task<byte[]> GetUserDataKeyAsync(
        SqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateProcedure(connection, "app.GetUserDek");
        Add(command, "@UserId", SqlDbType.UniqueIdentifier, userId);
        var encryptedDek = await command.ExecuteScalarAsync(cancellationToken);
        if (encryptedDek is not byte[] encrypted)
        {
            throw new InvalidOperationException($"No protected data key exists for user {userId}.");
        }

        return cryptoService.UnprotectUserDataKey(encrypted);
    }

    private static Guid? GetNullableGuid(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static string? GetNullableString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
