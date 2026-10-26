namespace SmartX.Api.Models;

using System.Text.Json.Serialization;

public sealed record TelemetryPacket<T>(
    [property: JsonRequired] Guid SensorId,
    [property: JsonRequired] T Value,
    [property: JsonRequired] DateTimeOffset Timestamp,
    [property: JsonRequired] string Unit,
    long? StateVersion = null);
