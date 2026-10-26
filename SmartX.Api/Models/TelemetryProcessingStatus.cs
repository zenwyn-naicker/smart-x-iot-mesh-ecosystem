namespace SmartX.Api.Models;

public sealed record TelemetryProcessingStatus(
    int Queued,
    int Capacity,
    long Processed,
    long Failed);
