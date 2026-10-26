namespace SmartX.Web.Models;

public sealed class CommandStreamViewModel
{
    public IReadOnlyList<CommandStreamDeviceViewModel> Devices { get; set; } = [];
    public IReadOnlyList<TelemetryReadingViewModel> Readings { get; set; } = [];
    public IReadOnlyList<ActiveIssueViewModel> ActiveIssues { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }
    public string? Error { get; set; }
    public CommandStreamFilter Filters { get; set; } = new();
    public IReadOnlyList<ActionSuggestionViewModel> Suggestions { get; set; } = [];
    public IReadOnlyList<DeviceCommandViewModel> CommandHistory { get; set; } = [];
    public IReadOnlyDictionary<Guid, string> DeviceNames { get; set; } = new Dictionary<Guid, string>();
}

public sealed record CommandStreamDeviceViewModel(
    Guid Id, string DeviceIdentifier, string DeploymentPath,
    SensorCategory Category, SensorStatus Status, DateTimeOffset? LastHeartbeat,
    ActuatorStateViewModel? ActuatorState);

public sealed record ActuatorStateViewModel(bool IsOn, DateTimeOffset UpdatedAt, long Version);
