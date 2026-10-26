using System.ComponentModel.DataAnnotations;

namespace SmartX.Api.Models;

public enum CommandStreamAlertFilter
{
    All,
    Active,
    Telemetry,
    Disconnected
}

public sealed class CommandStreamQuery
{
    [StringLength(200)]
    public string? Search { get; set; }

    [EnumDataType(typeof(SensorCategory))]
    public SensorCategory? Category { get; set; }

    [EnumDataType(typeof(CommandStreamAlertFilter))]
    public CommandStreamAlertFilter Alert { get; set; }

    [Range(1, 1000)]
    public int Limit { get; set; } = 200;
}

public sealed record CommandStreamDevice(
    Guid Id,
    string DeviceIdentifier,
    string DeploymentPath,
    SensorCategory Category,
    SensorStatus Status,
    DateTimeOffset? LastHeartbeat,
    ActuatorState? ActuatorState);

public sealed record CommandStreamResponse(
    IReadOnlyList<CommandStreamDevice> Devices,
    IReadOnlyList<TelemetryReadingResponse> Readings,
    IReadOnlyList<ActiveIssue> ActiveIssues,
    DateTimeOffset GeneratedAt);
