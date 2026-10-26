using System.ComponentModel.DataAnnotations;

namespace SmartX.Web.Models;

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

public sealed class RegisterSensorViewModel
{
    [Required]
    [Display(Name = "MAC address or unique identifier")]
    [RegularExpression(
        @"^[A-Za-z0-9][A-Za-z0-9:_-]{2,49}$",
        ErrorMessage = "Enter a valid identifier.")]
    public string DeviceIdentifier { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string Facility { get; set; } = string.Empty;

    [Required]
    [StringLength(60)]
    public string Zone { get; set; } = string.Empty;

    [Display(Name = "Sub-zone")]
    [StringLength(60)]
    public string? SubZone { get; set; }

    [Required]
    [StringLength(60)]
    public string Node { get; set; } = string.Empty;

    [Required]
    [EnumDataType(typeof(SensorCategory), ErrorMessage = "Select a valid sensor category.")]
    public SensorCategory Category { get; set; }
}

public sealed class SensorViewModel
{
    public Guid Id { get; set; }

    public string DeviceIdentifier { get; set; } = string.Empty;

    public string Facility { get; set; } = string.Empty;

    public string Zone { get; set; } = string.Empty;

    public string? SubZone { get; set; }

    public string Node { get; set; } = string.Empty;

    public string DeploymentPath { get; set; } = string.Empty;

    public SensorCategory Category { get; set; }

    public SensorStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastHeartbeat { get; set; }
}

public sealed class TelemetryFormViewModel
{
    [Required]
    [Display(Name = "Sensor")]
    public Guid SensorId { get; set; }

    [Required]
    [Display(Name = "Sensor value")]
    public string Value { get; set; } = string.Empty;

    public IReadOnlyList<SensorViewModel> Sensors { get; set; } = [];

    public bool SensorsUnavailable { get; set; }
}

public sealed class TelemetryReadingViewModel
{
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public string DataType { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; }

    public bool IsAnomaly { get; set; }

    public string? AnomalyMessage { get; set; }

    public double? LowerThreshold { get; set; }

    public double? UpperThreshold { get; set; }
}

public sealed class ActiveIssueViewModel
{
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public string DeviceIdentifier { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;

    public string IssueType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public int Occurrences { get; set; }
}

public sealed class SensorDashboardItemViewModel
{
    public Guid Id { get; set; }

    public string DeviceIdentifier { get; set; } = string.Empty;

    public string DeploymentPath { get; set; } = string.Empty;

    public SensorCategory Category { get; set; }

    public SensorStatus Status { get; set; }

    public DateTimeOffset? LastHeartbeat { get; set; }
}

public sealed class DashboardViewModel
{
    public Guid InstanceId { get; set; }

    public int TotalSensors { get; set; }

    public int ConnectedSensors { get; set; }

    public int WarningSensors { get; set; }

    public int DisconnectedSensors { get; set; }

    public int AggregatePowerWatts { get; set; }

    public IReadOnlyList<SensorDashboardItemViewModel> Sensors
    { get; set; } = [];

    public IReadOnlyList<TelemetryReadingViewModel> RecentReadings
    { get; set; } = [];

    public IReadOnlyList<ActiveIssueViewModel> ActiveIssues
    { get; set; } = [];

    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class AttachmentViewModel
{
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long Length { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class SensorDetailsPageViewModel
{
    public required SensorViewModel Sensor { get; set; }

    public IReadOnlyList<AttachmentViewModel> Attachments
    { get; set; } = [];
}
