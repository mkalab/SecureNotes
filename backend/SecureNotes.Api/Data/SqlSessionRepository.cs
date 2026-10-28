using System.Data;
using Microsoft.Data.SqlClient;

namespace SecureNotes.Api.Data;

public sealed class SqlSessionRepository(IConfiguration configuration) : ISessionRepository
{
    public async Task CreateAsync(
        Guid userId,
        byte[] sessionIdHash,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.CreateUserSession", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@SessionIdHash", SqlDbType.Binary, 32) { Value = sessionIdHash });
        command.Parameters.Add(new SqlParameter("@ExpiresAtUtc", SqlDbType.DateTime2) { Value = expiresAtUtc });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ValidateAndTouchAsync(
        byte[] sessionIdHash,
        DateTime nowUtc,
        int idleTimeoutMinutes,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.ValidateAndTouchUserSession", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@SessionIdHash", SqlDbType.Binary, 32) { Value = sessionIdHash });
        command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        command.Parameters.Add(new SqlParameter("@IdleTimeoutMinutes", SqlDbType.Int) { Value = idleTimeoutMinutes });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<bool> RecordSecurityViolationAsync(
        byte[] sessionIdHash,
        DateTime nowUtc,
        string action,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.RecordSessionSecurityViolation", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@SessionIdHash", SqlDbType.Binary, 32) { Value = sessionIdHash });
        command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        command.Parameters.Add(new SqlParameter("@Action", SqlDbType.VarChar, 80) { Value = action });
        command.Parameters.Add(new SqlParameter("@IpAddress", SqlDbType.VarChar, 45)
        {
            Value = (object?)ipAddress ?? DBNull.Value
        });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512)
        {
            Value = (object?)userAgent ?? DBNull.Value
        });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task RevokeAsync(byte[] sessionIdHash, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.RevokeUserSession", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@SessionIdHash", SqlDbType.Binary, 32) { Value = sessionIdHash });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The 'ConnectionStrings:SecureNotes' setting is required for database operations.");
        }

        return new SqlConnection(connectionString);
    }
}
