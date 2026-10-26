using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class ActiveIssueServiceTests
{
    [Fact]
    public void RepeatedAnomaliesStayUniqueAndCanRecurAfterRecovery()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor();
        repository.Add(sensor);
        var issues = new ActiveIssueService();
        var telemetry = new TelemetryService(repository, issues, new DeviceCommandService(repository, new UserActivityService(repository, issues)));
        var start = DateTimeOffset.UtcNow;

        telemetry.IngestEnvironmental(new(sensor.Id, 44f, start, "C"));
        var first = Assert.Single(issues.GetActiveIssues());
        telemetry.IngestEnvironmental(new(sensor.Id, 45f, start.AddSeconds(1), "C"));
        var repeated = Assert.Single(issues.GetActiveIssues());

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(start, repeated.FirstSeen);
        Assert.Equal(start.AddSeconds(1), repeated.LastSeen);
        Assert.Equal(2, repeated.Occurrences);

        telemetry.IngestEnvironmental(new(sensor.Id, 22f, start.AddSeconds(2), "C"));
        Assert.Empty(issues.GetActiveIssues());

        telemetry.IngestEnvironmental(new(sensor.Id, 44f, start.AddSeconds(3), "C"));
        var recurrence = Assert.Single(issues.GetActiveIssues());
        Assert.NotEqual(first.Id, recurrence.Id);
        Assert.Equal(1, recurrence.Occurrences);
        Assert.Equal(start.AddSeconds(3), recurrence.FirstSeen);
    }

    [Fact]
    public void DashboardRefreshesDoNotDuplicateDisconnectsOrCountNewOccurrences()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor();
        repository.Add(sensor);
        var issues = new ActiveIssueService();
        var telemetry = new TelemetryService(repository, issues, new DeviceCommandService(repository, new UserActivityService(repository, issues)));
        var dashboard = new DashboardService(repository, telemetry, issues);

        var first = Assert.Single(dashboard.GetDashboard().ActiveIssues);
        var repeatedDashboard = dashboard.GetDashboard();
        var repeated = Assert.Single(repeatedDashboard.ActiveIssues);

        Assert.Equal("Disconnect", repeated.IssueType);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.FirstSeen, repeated.FirstSeen);
        Assert.True(repeated.LastSeen >= first.LastSeen);
        Assert.Equal(1, repeated.Occurrences);
        Assert.Equal(1, repeatedDashboard.DisconnectedSensors);

        telemetry.IngestEnvironmental(new(sensor.Id, 22f, DateTimeOffset.UtcNow, "C"));
        Assert.Empty(issues.GetActiveIssues());
        var recovered = dashboard.GetDashboard();
        Assert.Empty(recovered.ActiveIssues);
        Assert.Equal(1, recovered.ConnectedSensors);

        repository.GetById(sensor.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-1);
        var recurrence = Assert.Single(dashboard.GetDashboard().ActiveIssues);
        Assert.NotEqual(first.Id, recurrence.Id);
        Assert.Equal(1, recurrence.Occurrences);
    }

    [Fact]
    public void ReconnectionClearsOnlyDisconnectWhileAnomalyRemains()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor();
        repository.Add(sensor);
        var issues = new ActiveIssueService();
        var telemetry = new TelemetryService(repository, issues, new DeviceCommandService(repository, new UserActivityService(repository, issues)));
        var dashboard = new DashboardService(repository, telemetry, issues);

        telemetry.IngestEnvironmental(new(
            sensor.Id, 44f, DateTimeOffset.UtcNow.AddMinutes(-1), "C"));
        Assert.Equal(2, dashboard.GetDashboard().ActiveIssues.Count);

        telemetry.IngestEnvironmental(new(sensor.Id, 45f, DateTimeOffset.UtcNow, "C"));
        var result = dashboard.GetDashboard();

        Assert.Equal("Telemetry", Assert.Single(result.ActiveIssues).IssueType);
        Assert.Equal(1, result.WarningSensors);
        Assert.Equal(0, result.DisconnectedSensors);
    }

    [Fact]
    public void AlertKeysKeepDevicesAndIssueTypesIndependent()
    {
        var issues = new ActiveIssueService();
        var first = CreateSensor();
        var second = CreateSensor();
        var timestamp = DateTimeOffset.UtcNow;

        issues.Record(first, "Telemetry", "High temperature.", timestamp);
        issues.Record(first, "Disconnect", "Heartbeat overdue.", timestamp);
        issues.Record(second, "Telemetry", "High temperature.", timestamp);
        issues.Resolve(first.Id, "Telemetry");
        issues.Resolve(first.Id, "Telemetry");

        var active = issues.GetActiveIssues();
        Assert.Equal(2, active.Count);
        Assert.Contains(active, issue =>
            issue.SensorId == first.Id && issue.IssueType == "Disconnect");
        Assert.Contains(active, issue =>
            issue.SensorId == second.Id && issue.IssueType == "Telemetry");
    }

    [Fact]
    public void ConcurrentAlertsStayUniqueAndSnapshotsCannotChangeStoredDetails()
    {
        var issues = new ActiveIssueService();
        var sensor = CreateSensor();
        var timestamp = DateTimeOffset.UtcNow;

        Parallel.For(0, 100, _ =>
            issues.Record(sensor, "Telemetry", "High temperature.", timestamp));

        var snapshot = Assert.Single(issues.GetActiveIssues());
        Assert.Equal(100, snapshot.Occurrences);
        snapshot.Occurrences = 0;
        snapshot.Message = "Changed externally.";

        issues.Record(sensor, "Telemetry", "Latest warning.", timestamp.AddSeconds(1));
        issues.Record(sensor, "Telemetry", "Older warning.", timestamp.AddSeconds(-1));
        var stored = Assert.Single(issues.GetActiveIssues());

        Assert.Equal(102, stored.Occurrences);
        Assert.Equal("Latest warning.", stored.Message);
        Assert.Equal(timestamp.AddSeconds(1), stored.LastSeen);
    }

    [Fact]
    public void DashboardInstanceIsStableUntilGatewayServicesRestart()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor();
        repository.Add(sensor);
        var issues = new ActiveIssueService();
        var telemetry = new TelemetryService(repository, issues,
            new DeviceCommandService(repository, new UserActivityService(repository, issues)));
        var dashboard = new DashboardService(repository, telemetry, issues);
        var initial = dashboard.GetDashboard();

        telemetry.IngestEnvironmental(new(sensor.Id, 22f, DateTimeOffset.UtcNow, "C"));
        var recovered = dashboard.GetDashboard();
        var restarted = new DashboardService(repository, telemetry, issues).GetDashboard();

        Assert.NotEqual(Guid.Empty, initial.InstanceId);
        Assert.Equal(initial.InstanceId, recovered.InstanceId);
        Assert.NotEqual(initial.InstanceId, restarted.InstanceId);
    }

    private static Sensor CreateSensor()
    {
        return new Sensor
        {
            DeviceIdentifier = Guid.NewGuid().ToString(),
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1",
            Category = SensorCategory.Environmental
        };
    }
}
