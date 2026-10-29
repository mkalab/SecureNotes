using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SecureNotes.Api.Models;

namespace SecureNotes.Api.Data;

public sealed class SqlTwoFactorRepository(IConfiguration configuration) : ITwoFactorRepository
{
    public async Task<byte[]> GetWrappedDataKeyAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetUserDek");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as byte[]
            ?? throw new InvalidOperationException("The account has no wrapped data key.");
    }

    public async Task<TwoFactorData?> GetDataAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetUserTwoFactorData");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TwoFactorData(
            reader.GetBoolean(reader.GetOrdinal("TwoFactorEnabled")),
            NullableBytes(reader, "TwoFactorSecret"),
            NullableBytes(reader, "TwoFactorPendingSecret"),
            reader.IsDBNull(reader.GetOrdinal("TwoFactorLastStep"))
                ? null
                : reader.GetInt64(reader.GetOrdinal("TwoFactorLastStep")));
    }

    public async Task<bool> SetPendingSecretAsync(
        Guid userId,
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.SetPendingTwoFactorSecret");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@Secret", SqlDbType.VarBinary, 128) { Value = ciphertext });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task EnableAsync(
        Guid userId,
        byte[] ciphertext,
        long lastStep,
        IReadOnlyList<string> recoveryCodeHashes,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.EnableTwoFactor");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@Secret", SqlDbType.VarBinary, 128) { Value = ciphertext });
        command.Parameters.Add(new SqlParameter("@LastStep", SqlDbType.BigInt) { Value = lastStep });
        command.Parameters.Add(new SqlParameter("@RecoveryHashes", SqlDbType.NVarChar, -1)
        {
            Value = JsonSerializer.Serialize(recoveryCodeHashes)
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DisableAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.DisableTwoFactor");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<bool> CreateChallengeAsync(
        Guid userId,
        byte[] challengeHash,
        DateTime expiresAtUtc,
        string authProvider,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.CreateTwoFactorChallenge");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@ChallengeHash", SqlDbType.Binary, 32) { Value = challengeHash });
        command.Parameters.Add(new SqlParameter("@ExpiresAtUtc", SqlDbType.DateTime2) { Value = expiresAtUtc });
        command.Parameters.Add(new SqlParameter("@AuthProvider", SqlDbType.VarChar, 16) { Value = authProvider });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<TwoFactorChallenge?> GetChallengeAsync(
        byte[] challengeHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.GetTwoFactorChallenge");
        command.Parameters.Add(new SqlParameter("@ChallengeHash", SqlDbType.Binary, 32) { Value = challengeHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TwoFactorChallenge(
            reader.GetGuid(reader.GetOrdinal("UserId")),
            reader.GetString(reader.GetOrdinal("Email")),
            reader.GetString(reader.GetOrdinal("Role")),
            reader.GetString(reader.GetOrdinal("AuthProvider")),
            NullableBytes(reader, "TwoFactorSecret"),
            reader.IsDBNull(reader.GetOrdinal("TwoFactorLastStep"))
                ? null
                : reader.GetInt64(reader.GetOrdinal("TwoFactorLastStep")),
            NullableBytes(reader, "DekCiphertext"));
    }

    public async Task FailChallengeAsync(byte[] challengeHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.FailTwoFactorChallenge");
        command.Parameters.Add(new SqlParameter("@ChallengeHash", SqlDbType.Binary, 32) { Value = challengeHash });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ConsumeTotpChallengeAsync(
        byte[] challengeHash,
        Guid userId,
        long step,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ConsumeTwoFactorTotpChallenge");
        command.Parameters.Add(new SqlParameter("@ChallengeHash", SqlDbType.Binary, 32) { Value = challengeHash });
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@Step", SqlDbType.BigInt) { Value = step });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<bool> ConsumeManagementTotpAsync(
        Guid userId,
        long step,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ConsumeManagementTwoFactorTotp");
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@Step", SqlDbType.BigInt) { Value = step });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<bool> ConsumeRecoveryChallengeAsync(
        byte[] challengeHash,
        Guid userId,
        byte[] codeHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateProcedure(connection, "app.ConsumeTwoFactorRecoveryChallenge");
        command.Parameters.Add(new SqlParameter("@ChallengeHash", SqlDbType.Binary, 32) { Value = challengeHash });
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier) { Value = userId });
        command.Parameters.Add(new SqlParameter("@CodeHash", SqlDbType.Binary, 32) { Value = codeHash });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
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

    private static byte[]? NullableBytes(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<byte[]>(ordinal);
    }
}
