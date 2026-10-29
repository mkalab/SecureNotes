using System.Data;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public sealed class SqlAdminRepository(IConfiguration configuration) : IAdminRepository
{
    public async Task<IReadOnlyList<AdminUser>> ListUsersAsync(
        Guid adminUserId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminListUsers");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@Offset", SqlDbType.Int) { Value = offset });
        command.Parameters.Add(new SqlParameter("@Limit", SqlDbType.Int) { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<AdminUser>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new AdminUser(
                reader.GetGuid(reader.GetOrdinal("UserId")),
                reader.GetString(reader.GetOrdinal("Email")),
                reader.GetString(reader.GetOrdinal("Role")),
                reader.GetBoolean(reader.GetOrdinal("IsDisabled")),
                reader.GetInt32(reader.GetOrdinal("FailedCount")),
                NullableDateTime(reader, "LockedUntil"),
                reader.GetBoolean(reader.GetOrdinal("TwoFactorEnabled")),
                DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), DateTimeKind.Utc)));
        }

        return users;
    }

    public async Task<bool> SetUserDisabledAsync(
        Guid adminUserId,
        Guid targetUserId,
        bool disabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminSetUserDisabled");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@TargetUserId", SqlDbType.UniqueIdentifier) { Value = targetUserId });
        command.Parameters.Add(new SqlParameter("@Disabled", SqlDbType.Bit) { Value = disabled });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<int> RevokeUserSessionsAsync(
        Guid adminUserId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminRevokeUserSessions");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@TargetUserId", SqlDbType.UniqueIdentifier) { Value = targetUserId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ListAuditAsync(
        Guid adminUserId,
        long? beforeAuditLogId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminListAuditLogs");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@BeforeAuditLogId", SqlDbType.BigInt)
        {
            Value = beforeAuditLogId is null ? DBNull.Value : beforeAuditLogId.Value
        });
        command.Parameters.Add(new SqlParameter("@Limit", SqlDbType.Int) { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AdminAuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AdminAuditEntry(
                reader.GetInt64(reader.GetOrdinal("AuditLogId")),
                NullableGuid(reader, "UserId"),
                NullableString(reader, "Email"),
                reader.GetString(reader.GetOrdinal("Action")),
                NullableString(reader, "ResourceType"),
                NullableGuid(reader, "ResourceId"),
                NullableString(reader, "IpAddress"),
                NullableString(reader, "UserAgent"),
                DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), DateTimeKind.Utc)));
        }

        return entries;
    }

    public async Task<IReadOnlyList<AdminShare>> ListSharesAsync(
        Guid adminUserId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminListShares");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@Limit", SqlDbType.Int) { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shares = new List<AdminShare>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shares.Add(new AdminShare(
                reader.GetGuid(reader.GetOrdinal("ShareId")),
                reader.GetGuid(reader.GetOrdinal("NoteId")),
                reader.GetGuid(reader.GetOrdinal("UserId")),
                reader.GetString(reader.GetOrdinal("Email")),
                reader.GetString(reader.GetOrdinal("Permission")),
                DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("ExpiresAt")), DateTimeKind.Utc),
                NullableDateTime(reader, "RevokedAt") is { } revoked
                    ? DateTime.SpecifyKind(revoked, DateTimeKind.Utc)
                    : null,
                DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("CreatedAt")), DateTimeKind.Utc)));
        }

        return shares;
    }

    public async Task<bool> RevokeShareAsync(
        Guid adminUserId,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.AdminRevokeShare");
        command.Parameters.Add(new SqlParameter("@AdminUserId", SqlDbType.UniqueIdentifier) { Value = adminUserId });
        command.Parameters.Add(new SqlParameter("@ShareId", SqlDbType.UniqueIdentifier) { Value = shareId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("The SecureNotes database connection is required.");
        }

        return new SqlConnection(connectionString);
    }

    private static SqlCommand CreateProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static DateTime? NullableDateTime(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static Guid? NullableGuid(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static string? NullableString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
