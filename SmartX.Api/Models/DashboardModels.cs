namespace SmartX.Api.Models;

public sealed record SensorDashboardItem(
    Guid Id,
    string DeviceIdentifier,
    string DeploymentPath,
    SensorCategory Category,
    SensorStatus Status,
    DateTimeOffset? LastHeartbeat);

public sealed record DashboardResponse(
    int TotalSensors,
    int ConnectedSensors,
    int WarningSensors,
    int DisconnectedSensors,
    int AggregatePowerWatts,
    IReadOnlyList<SensorDashboardItem> Sensors,
    IReadOnlyList<TelemetryReadingResponse> RecentReadings,
    IReadOnlyList<ActiveIssue> ActiveIssues,
    DateTimeOffset GeneratedAt)
{
    public Guid InstanceId { get; init; }
}
