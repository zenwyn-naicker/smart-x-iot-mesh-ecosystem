using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed class CommandStreamService(
    DashboardService dashboardService,
    TelemetryService telemetryService,
    DeviceCommandService commandService)
{
    public CommandStreamResponse GetStream(CommandStreamQuery query)
    {
        var dashboard = dashboardService.GetDashboard();
        var search = query.Search?.Trim();
        var alertSensorIds = dashboard.ActiveIssues
            .Where(issue => query.Alert switch
            {
                CommandStreamAlertFilter.Telemetry => issue.IssueType == "Telemetry",
                CommandStreamAlertFilter.Disconnected => issue.IssueType == "Disconnect",
                _ => true
            })
            .Select(issue => issue.SensorId)
            .ToHashSet();

        var devices = dashboard.Sensors
            .Where(sensor => query.Category is null || sensor.Category == query.Category)
            .Where(sensor => string.IsNullOrEmpty(search) ||
                sensor.DeviceIdentifier.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                sensor.DeploymentPath.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(sensor => query.Alert == CommandStreamAlertFilter.All || alertSensorIds.Contains(sensor.Id))
            .Select(sensor => new CommandStreamDevice(
                sensor.Id, sensor.DeviceIdentifier, sensor.DeploymentPath, sensor.Category,
                sensor.Status, sensor.LastHeartbeat, commandService.GetState(sensor.Id)))
            .ToArray();
        var sensorIds = devices.Select(device => device.Id).ToHashSet();

        return new CommandStreamResponse(
            devices,
            telemetryService.GetHistory(limit: query.Limit, sensorIds: sensorIds),
            dashboard.ActiveIssues.Where(issue => sensorIds.Contains(issue.SensorId)).ToArray(),
            dashboard.GeneratedAt);
    }
}
