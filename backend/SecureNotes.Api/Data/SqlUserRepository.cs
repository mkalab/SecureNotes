using System.Data;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public sealed class SqlUserRepository(IConfiguration configuration) : IUserRepository
{
    public async Task<UserCredential> CreateAsync(
        string email,
        byte[]? passwordHash,
        byte[]? passwordSalt,
        int? passwordIterations,
        byte[]? dekCiphertext,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.CreateUser", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 320) { Value = email });
        command.Parameters.Add(new SqlParameter("@PasswordHash", SqlDbType.VarBinary, 64)
        {
            Value = passwordHash is null ? DBNull.Value : passwordHash
        });
        command.Parameters.Add(new SqlParameter("@PasswordSalt", SqlDbType.VarBinary, 32)
        {
            Value = passwordSalt is null ? DBNull.Value : passwordSalt
        });
        command.Parameters.Add(new SqlParameter("@PasswordIterations", SqlDbType.Int)
        {
            Value = passwordIterations is null ? DBNull.Value : passwordIterations
        });
        command.Parameters.Add(new SqlParameter("@DekCiphertext", SqlDbType.VarBinary, -1)
        {
            Value = dekCiphertext is null ? DBNull.Value : dekCiphertext
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The create-user procedure returned no user.");
        }

        return ReadUser(reader);
    }

    public async Task<UserCredential?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.GetUserByEmail", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 320) { Value = email });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<UserCredential?> GetByGoogleSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.GetGoogleUserBySubject", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@Subject", SqlDbType.NVarChar, 255) { Value = subject });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    public async Task<UserCredential> CreateGoogleUserAsync(
        string email,
        string subject,
        byte[] dekCiphertext,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.CreateGoogleUser", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 320) { Value = email });
        command.Parameters.Add(new SqlParameter("@Subject", SqlDbType.NVarChar, 255) { Value = subject });
        command.Parameters.Add(new SqlParameter("@DekCiphertext", SqlDbType.VarBinary, -1) { Value = dekCiphertext });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The create-Google-user procedure returned no user.");
        }

        return ReadUser(reader);
    }

    public async Task<UserCredential> LinkGoogleLoginAsync(
        Guid userId,
        string subject,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.LinkGoogleLogin", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@Subject", SqlDbType.NVarChar, 255) { Value = subject });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The link-Google-login procedure returned no user.");
        }

        return ReadUser(reader);
    }

    public async Task RecordFailedLoginAsync(string email, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.RecordFailedLogin", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@Email", SqlDbType.NVarChar, 320) { Value = email });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetFailedLoginAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("app.ResetFailedLogin", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
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

    private static UserCredential ReadUser(SqlDataReader reader)
    {
        return new UserCredential(
            reader.GetGuid(reader.GetOrdinal("UserId")),
            reader.GetString(reader.GetOrdinal("Email")),
            reader.GetString(reader.GetOrdinal("NormalizedEmail")),
            GetNullableBytes(reader, "PasswordHash"),
            GetNullableBytes(reader, "PasswordSalt"),
            GetNullableInt32(reader, "PasswordIterations"),
            GetNullableBytes(reader, "DekCiphertext"),
            reader.GetString(reader.GetOrdinal("Role")),
            reader.GetInt32(reader.GetOrdinal("FailedCount")),
            GetNullableDateTime(reader, "LockedUntil"),
            reader.GetBoolean(reader.GetOrdinal("IsDisabled")),
            reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            reader.GetDateTime(reader.GetOrdinal("UpdatedAt")));
    }

    private static byte[]? GetNullableBytes(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);
    }

    private static int? GetNullableInt32(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTime? GetNullableDateTime(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }
}
