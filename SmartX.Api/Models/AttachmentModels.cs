namespace SmartX.Api.Models;

public sealed class AttachmentRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SensorId { get; init; }

    public required string OriginalFileName { get; init; }

    public required string ContentType { get; init; }

    public required string StoredPath { get; init; }

    public long Length { get; init; }

    public DateTimeOffset UploadedAt { get; init; }
}

public sealed record AttachmentResponse(
    Guid Id,
    Guid SensorId,
    string FileName,
    string ContentType,
    long Length,
    DateTimeOffset UploadedAt);