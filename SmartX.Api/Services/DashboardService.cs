using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class DashboardService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly TelemetryService _telemetryService;
    private readonly ActiveIssueService _activeIssueService;
    private readonly Guid _instanceId = Guid.NewGuid();

    public DashboardService(
        ISensorRepository sensorRepository,
        TelemetryService telemetryService,
        ActiveIssueService activeIssueService)
    {
        _sensorRepository = sensorRepository;
        _telemetryService = telemetryService;
        _activeIssueService = activeIssueService;
    }

    public DashboardResponse GetDashboard()
    {
        var now = DateTimeOffset.UtcNow;
        var sensors = _sensorRepository.GetAll();
        HashSet<Guid> disconnectedSensorIds = [];

        foreach (var sensor in sensors)
        {
            var disconnected =
                sensor.LastHeartbeat is null ||
                now - sensor.LastHeartbeat >
                TimeSpan.FromSeconds(30);

            if (disconnected)
            {
                disconnectedSensorIds.Add(sensor.Id);
                _activeIssueService.Record(
                    sensor,
                    "Disconnect",
                    sensor.LastHeartbeat is null
                        ? "No heartbeat has been received."
                        : "The sensor heartbeat is overdue.",
                    now,
                    countOccurrence: false);
            }
            else
            {
                _activeIssueService.Resolve(sensor.Id, "Disconnect");
            }
        }

        var issues = _activeIssueService.GetActiveIssues();
        var sensorsWithIssues = issues.Select(issue => issue.SensorId).ToHashSet();
        List<SensorDashboardItem> sensorItems = [];

        foreach (var sensor in sensors)
        {
            var status = disconnectedSensorIds.Contains(sensor.Id)
                ? SensorStatus.Disconnected
                : sensorsWithIssues.Contains(sensor.Id)
                    ? SensorStatus.Warning
                    : SensorStatus.Connected;

            sensorItems.Add(new SensorDashboardItem(
                sensor.Id,
                sensor.DeviceIdentifier,
                sensor.DeploymentPath,
                sensor.Category,
                status,
                sensor.LastHeartbeat));
        }

        return new DashboardResponse(
            sensorItems.Count,
            sensorItems.Count(item =>
                item.Status == SensorStatus.Connected),
            sensorItems.Count(item =>
                item.Status == SensorStatus.Warning),
            sensorItems.Count(item =>
                item.Status == SensorStatus.Disconnected),
            _telemetryService.GetAggregatePower().Watts,
            sensorItems
                .OrderBy(item => item.DeviceIdentifier)
                .ToList(),
            _telemetryService.GetHistory(limit: 300),
            issues,
            now)
        {
            InstanceId = _instanceId
        };
    }
}
