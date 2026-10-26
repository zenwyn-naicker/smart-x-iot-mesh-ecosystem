namespace SmartX.Api.Models;

public sealed record TelemetryReadingResponse(
    Guid Id,
    Guid SensorId,
    string DataType,
    string Value,
    string Unit,
    DateTimeOffset Timestamp,
    bool IsAnomaly,
    string? AnomalyMessage,
    double? LowerThreshold,
    double? UpperThreshold);

public sealed record TelemetryHistoryRangeResponse(
    IReadOnlyList<TelemetryReadingResponse> Readings,
    bool HasMore);

public sealed class ActiveIssue
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SensorId { get; init; }

    public required string DeviceIdentifier { get; init; }

    public required string Severity { get; init; }

    public required string IssueType { get; init; }

    public required string Message { get; set; }

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; set; }

    public int Occurrences { get; set; } = 1;
}

public sealed record PowerSnapshot(
    Guid SensorId,
    PowerReading Reading,
    DateTimeOffset Timestamp);
