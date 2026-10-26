namespace SmartX.Api.Models;

public sealed record TelemetryAcceptanceResponse(
    Guid SensorId,
    DateTimeOffset AcceptedAt,
    string Message,
    Guid PacketId);
