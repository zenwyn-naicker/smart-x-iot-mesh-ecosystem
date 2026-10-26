namespace SmartX.Api.Models;

public sealed record TelemetryProcessingEvent(
    Guid PacketId,
    Guid SensorId,
    long ArrivalOrder,
    long ProcessingOrder,
    bool Critical,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset ProcessedAt,
    double QueueLatencyMilliseconds,
    bool Succeeded);
