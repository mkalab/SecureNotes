namespace SecureNotes.Api.Data;

public interface IAuditRepository
{
    Task RecordAsync(
        Guid? userId,
        string action,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}
