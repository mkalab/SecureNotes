namespace SecureNotes.Api.Attachments;

public sealed class AttachmentOptions
{
    public bool Enabled { get; set; } = true;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxFilesPerNote { get; set; } = 20;
    public long MaxTotalBytesPerUser { get; set; } = 100 * 1024 * 1024;
    public string StoragePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SecureNotes",
        "attachments");

    public bool IsValid() =>
        MaxFileBytes is > 0 and <= 100 * 1024 * 1024
        && MaxFilesPerNote is > 0 and <= 1000
        && MaxTotalBytesPerUser >= MaxFileBytes
        && MaxTotalBytesPerUser <= 10L * 1024 * 1024 * 1024
        && Path.IsPathFullyQualified(StoragePath);
}
