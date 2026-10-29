using System.Data;
using Microsoft.Data.SqlClient;

namespace SecureNotes.Api.Data;

public sealed class SqlAuditRepository(IConfiguration configuration) : IAuditRepository
{
    public async Task RecordAsync(
        Guid? userId,
        string action,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken,
        string? resourceType = "authentication",
        Guid? resourceId = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.AddAuditLog", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier)
        {
            Value = userId is null ? DBNull.Value : userId.Value
        });
        command.Parameters.Add(new SqlParameter("@Action", SqlDbType.VarChar, 80) { Value = action });
        command.Parameters.Add(new SqlParameter("@ResourceType", SqlDbType.VarChar, 80)
        {
            Value = resourceType is null ? DBNull.Value : resourceType
        });
        command.Parameters.Add(new SqlParameter("@ResourceId", SqlDbType.UniqueIdentifier)
        {
            Value = resourceId is null ? DBNull.Value : resourceId.Value
        });
        command.Parameters.Add(new SqlParameter("@IpAddress", SqlDbType.VarChar, 45)
        {
            Value = ipAddress is null ? DBNull.Value : ipAddress
        });
        command.Parameters.Add(new SqlParameter("@UserAgent", SqlDbType.NVarChar, 512)
        {
            Value = userAgent is null ? DBNull.Value : userAgent
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("SecureNotes");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The 'ConnectionStrings:SecureNotes' setting is required for audit operations.");
        }

        return new SqlConnection(connectionString);
    }
}
