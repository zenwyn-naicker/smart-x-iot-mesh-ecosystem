using System.ComponentModel.DataAnnotations;

namespace SmartX.Api.Models;

public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
}

public enum SensorStatus
{
    Connected,
    Warning,
    Disconnected
}

public enum DeploymentLevel
{
    Facility,
    Zone,
    SubZone,
    Node
}

public sealed class DeploymentNode
{
    public required string Name { get; init; }

    public required DeploymentLevel Level { get; init; }

    public List<DeploymentNode> Children { get; init; } = [];
}

public sealed class RegisterSensorRequest
{
    [Required]
    [RegularExpression(
        @"^[A-Za-z0-9][A-Za-z0-9:_-]{2,49}$",
        ErrorMessage = "Enter a valid MAC address or unique identifier.")]
    public string DeviceIdentifier { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string Facility { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string Zone { get; set; } = string.Empty;

    [StringLength(60)]
    public string? SubZone { get; set; }

    [Required]
    [StringLength(60)]
    public string Node { get; set; } = string.Empty;

    [Required]
    [EnumDataType(typeof(SensorCategory), ErrorMessage = "Select a valid sensor category.")]
    public SensorCategory Category { get; set; }
}

public sealed class Sensor
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string DeviceIdentifier { get; init; }

    public required string Facility { get; init; }

    public required string Zone { get; init; }

    public string? SubZone { get; init; }

    public required string Node { get; init; }

    public required SensorCategory Category { get; init; }

    public SensorStatus Status { get; set; } = SensorStatus.Connected;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastHeartbeat { get; set; }

    public string DeploymentPath =>
        string.Join(
            " / ",
            new[] { Facility, Zone, SubZone, Node }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record SensorResponse(
    Guid Id,
    string DeviceIdentifier,
    string Facility,
    string Zone,
    string? SubZone,
    string Node,
    string DeploymentPath,
    SensorCategory Category,
    SensorStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastHeartbeat)
{
    public ActuatorState? ActuatorState { get; init; }

    public static SensorResponse FromSensor(Sensor sensor)
    {
        return new SensorResponse(
            sensor.Id,
            sensor.DeviceIdentifier,
            sensor.Facility,
            sensor.Zone,
            sensor.SubZone,
            sensor.Node,
            sensor.DeploymentPath,
            sensor.Category,
            sensor.Status,
            sensor.CreatedAt,
            sensor.LastHeartbeat);
    }
}
