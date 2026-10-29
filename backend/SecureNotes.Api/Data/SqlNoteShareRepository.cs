using System.Data;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public sealed class SqlNoteShareRepository(IConfiguration configuration) : INoteShareRepository
{
    public async Task CreateAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        byte[] tokenHash,
        string permission,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.CreateNoteShare");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        command.Parameters.Add(new SqlParameter("@ShareId", SqlDbType.UniqueIdentifier) { Value = shareId });
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
        command.Parameters.Add(new SqlParameter("@Permission", SqlDbType.VarChar, 4) { Value = permission });
        command.Parameters.Add(new SqlParameter("@ExpiresAt", SqlDbType.DateTime2) { Value = expiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoteShare>> ListAsync(
        Guid userId,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ListNoteShares");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shares = new List<NoteShare>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shares.Add(new NoteShare(
                reader.GetGuid(reader.GetOrdinal("ShareId")),
                reader.GetString(reader.GetOrdinal("Permission")),
                ReadUtc(reader, "ExpiresAt"),
                reader.GetBoolean(reader.GetOrdinal("IsExpired")),
                reader.IsDBNull(reader.GetOrdinal("RevokedAt"))
                    ? null
                    : ReadUtc(reader, "RevokedAt"),
                ReadUtc(reader, "CreatedAt")));
        }

        return shares;
    }

    public async Task<bool> RevokeAsync(
        Guid userId,
        Guid noteId,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.RevokeNoteShare");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@NoteId", SqlDbType.UniqueIdentifier) { Value = noteId });
        command.Parameters.Add(new SqlParameter("@ShareId", SqlDbType.UniqueIdentifier) { Value = shareId });
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<NoteShareAccess?> GetActiveAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetActiveNoteShare");
        command.Parameters.Add(new SqlParameter("@TokenHash", SqlDbType.Binary, 32) { Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NoteShareAccess(
            reader.GetGuid(reader.GetOrdinal("ShareId")),
            reader.GetGuid(reader.GetOrdinal("UserId")),
            reader.GetGuid(reader.GetOrdinal("NoteId")),
            reader.GetString(reader.GetOrdinal("Permission")),
            ReadUtc(reader, "ExpiresAt"));
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The 'ConnectionStrings:SecureNotes' setting is required for share operations.");
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static SqlCommand CreateProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static DateTime ReadUtc(SqlDataReader reader, string columnName) =>
        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal(columnName)), DateTimeKind.Utc);
}
