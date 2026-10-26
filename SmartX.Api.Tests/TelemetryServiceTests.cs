using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class TelemetryServiceTests
{
    [Fact]
    public void HistoryCombinesTypedBatchesAndFiltersLatestReadings()
    {
        var repository = new InMemorySensorRepository();
        var environmental = AddSensor(repository, SensorCategory.Environmental);
        var power = AddSensor(repository, SensorCategory.PowerConsumption);
        var actuator = AddSensor(repository, SensorCategory.Actuator);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;

        var environmentalReadings = service.IngestEnvironmentalBatch(
        [
            new(environmental, 23f, start.AddSeconds(3), "C"),
            new(environmental, 21f, start.AddSeconds(1), "C"),
            new(environmental, 22f, start.AddSeconds(2), "C")
        ]);
        var powerReading = service.IngestPower(
            new(power, 1000, start, "W"));
        var actuatorReading = service.IngestActuator(
            new(actuator, true, start.AddSeconds(4), "state"));

        Assert.Equal(
            new[]
            {
                powerReading.Id,
                environmentalReadings[1].Id,
                environmentalReadings[2].Id,
                environmentalReadings[0].Id,
                actuatorReading.Id
            },
            service.GetHistory().Select(reading => reading.Id));
        Assert.Equal(
            new[] { environmentalReadings[2].Id, environmentalReadings[0].Id },
            service.GetHistory(environmental, 2).Select(reading => reading.Id));
        Assert.Equal(actuatorReading.Id, Assert.Single(service.GetHistory(limit: 0)).Id);
        Assert.Equal(environmentalReadings[0].Id, Assert.Single(service.GetHistory(environmental, 0)).Id);
        Assert.Equal(service.GetHistory(environmental, 2),
            service.GetHistory(environmental, 2, new HashSet<Guid> { environmental, power }));
        Assert.Empty(service.GetHistory(environmental, sensorIds: new HashSet<Guid> { power }));
        Assert.Empty(service.GetHistory(environmental, sensorIds: new HashSet<Guid>()));
    }

    [Fact]
    public void HistoryRetainsTenThousandReadingsAcrossMixedBatchSizes()
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, SensorCategory.Environmental);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;

        for (var index = 0; index < 150; index++)
        {
            service.IngestEnvironmental(new(sensor, 22f, start.AddSeconds(index), "C"));
        }

        Assert.Equal(150, service.GetHistory(limit: 1000).Count);

        var batch = Enumerable.Range(150, 9850)
            .Select(index => new TelemetryPacket<float>(
                sensor, 22f, start.AddSeconds(-index), "C"))
            .ToArray();

        service.IngestEnvironmentalBatch(batch);
        service.IngestEnvironmental(new(sensor, 22f, start.AddDays(-1), "C"));

        var history = service.GetHistory(limit: 1000);
        Assert.DoesNotContain(history, reading => reading.Timestamp == start);
        Assert.Contains(history, reading => reading.Timestamp == start.AddSeconds(1));
        Assert.Equal(1000, service.GetHistory(limit: 2000).Count);
        Assert.Equal(history, service.GetHistory(sensor, 2000));
        var range = service.GetHistoryRange(sensor, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 10000);
        Assert.Equal(10000, range.Readings.Count);
        Assert.False(range.HasMore);
        Assert.DoesNotContain(range.Readings, reading => reading.Timestamp == start);
    }

    [Fact]
    public void HistoryPreservesReadingsWithIdenticalTimestamps()
    {
        var repository = new InMemorySensorRepository();
        var environmental = AddSensor(repository, SensorCategory.Environmental);
        var power = AddSensor(repository, SensorCategory.PowerConsumption);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var timestamp = DateTimeOffset.UtcNow;

        var first = service.IngestEnvironmental(new(environmental, 21f, timestamp, "C"));
        var second = service.IngestPower(new(power, 1000, timestamp, "W"));
        var third = service.IngestEnvironmental(new(environmental, 22f, timestamp, "C"));

        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            service.GetHistory().Select(reading => reading.Id));
        Assert.Equal(
            new[] { second.Id, third.Id },
            service.GetHistory(limit: 2).Select(reading => reading.Id));
        Assert.Equal(
            new[] { first.Id, third.Id },
            service.GetHistory(environmental).Select(reading => reading.Id));
    }

    [Fact]
    public void HistoryEvictsOnlyExcessArrivalsWithIdenticalTimestamps()
    {
        var repository = new InMemorySensorRepository();
        var environmental = AddSensor(repository, SensorCategory.Environmental);
        var power = AddSensor(repository, SensorCategory.PowerConsumption);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var timestamp = DateTimeOffset.UtcNow;

        service.IngestEnvironmental(new(environmental, 21f, timestamp, "C"));
        var retained = service.IngestEnvironmental(new(environmental, 22f, timestamp, "C"));
        service.IngestPowerBatch(Enumerable.Range(0, 9999)
            .Select(_ => new TelemetryPacket<int>(power, 1000, timestamp, "W"))
            .ToArray());

        Assert.Equal(retained.Id, Assert.Single(service.GetHistory(environmental)).Id);
        Assert.Equal(1000, service.GetHistory(limit: 2000).Count);
        Assert.Empty(service.GetHistory(Guid.NewGuid()));
    }

    [Fact]
    public void DelayedReadingsCannotClearOrReopenANewerAlert()
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, SensorCategory.Environmental);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;

        service.IngestEnvironmental(new(sensor, 44f, start, "C"));
        service.IngestEnvironmental(new(sensor, 22f, start.AddSeconds(-1), "C"));
        service.IngestEnvironmental(new(sensor, 22f, start, "C"));

        Assert.Equal(1, Assert.Single(issues.GetActiveIssues()).Occurrences);
        Assert.Equal(start, repository.GetById(sensor)!.LastHeartbeat);

        service.IngestEnvironmental(new(sensor, 22f, start.AddSeconds(1), "C"));
        service.IngestEnvironmental(new(sensor, 45f, start, "C"));

        Assert.Empty(issues.GetActiveIssues());
        Assert.Equal(5, service.GetHistory().Count);
    }

    [Fact]
    public void DelayedPowerDoesNotReplaceTheLatestBaselineOrAggregate()
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, SensorCategory.PowerConsumption);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;

        service.IngestPower(new(sensor, 3000, start, "W"));
        service.IngestPower(new(sensor, 0, start.AddSeconds(-1), "W"));
        service.IngestPower(new(sensor, 0, start, "W"));

        Assert.Equal(3000, service.GetAggregatePower().Watts);
        var reading = service.IngestPower(new(sensor, 3100, start.AddSeconds(1), "W"));

        Assert.False(reading.IsAnomaly);
        Assert.Empty(issues.GetActiveIssues());
        Assert.Equal(3100, service.GetAggregatePower().Watts);
        Assert.Equal(4, service.GetHistory().Count);
    }

    [Fact]
    public void ConcurrentReadingsKeepTheNewestHeartbeatAndAlert()
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, SensorCategory.Environmental);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;

        Parallel.For(0, 100, index => service.IngestEnvironmental(
            new(sensor, index == 99 ? 44f : 22f, start.AddMilliseconds(index), "C")));

        Assert.Equal(start.AddMilliseconds(99), repository.GetById(sensor)!.LastHeartbeat);
        Assert.Equal(start.AddMilliseconds(99), Assert.Single(issues.GetActiveIssues()).LastSeen);
        Assert.Equal(100, service.GetHistory().Count);
    }

    [Theory]
    [InlineData(SensorCategory.Environmental)]
    [InlineData(SensorCategory.PowerConsumption)]
    [InlineData(SensorCategory.Actuator)]
    public void RepeatedBatchesProcessEachReadingOnce(SensorCategory category)
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, category);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var start = DateTimeOffset.UtcNow;
        var expected = new List<TelemetryReadingResponse>();

        for (var index = 0; index < 120; index++)
        {
            var timestamps = Enumerable.Range(0, index % 3)
                .Select(offset => start.AddMilliseconds(index * 3 + offset))
                .ToArray();
            var readings = category switch
            {
                SensorCategory.Environmental => service.IngestEnvironmentalBatch(timestamps
                    .Select(time => new TelemetryPacket<float>(sensor, 22f, time, "C")).ToArray()),
                SensorCategory.PowerConsumption => service.IngestPowerBatch(timestamps
                    .Select(time => new TelemetryPacket<int>(sensor, 1000, time, "W")).ToArray()),
                _ => service.IngestActuatorBatch(timestamps
                    .Select(time => new TelemetryPacket<bool>(sensor, true, time, "state")).ToArray())
            };

            Assert.Equal(timestamps, readings.Select(reading => reading.Timestamp));
            expected.AddRange(readings);
        }

        Assert.Equal(120, expected.Count);
        Assert.Equal(expected, service.GetHistory());
        var indexed = service.GetHistoryRange(sensor, start, expected[^1].Timestamp);
        Assert.Equal(expected, indexed.Readings);
        Assert.False(indexed.HasMore);
        Assert.Equal(expected.Count, expected.Select(reading => reading.Id).Distinct().Count());
        Assert.Equal(expected[^1].Timestamp, repository.GetById(sensor)!.LastHeartbeat);
    }

    [Theory]
    [InlineData(SensorCategory.Environmental)]
    [InlineData(SensorCategory.PowerConsumption)]
    [InlineData(SensorCategory.Actuator)]
    public void RejectedBatchDoesNotLeakIntoTheNextIngestion(SensorCategory category)
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, category);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var service = new TelemetryService(repository, issues, commands);
        var timestamp = DateTimeOffset.UtcNow;

        IReadOnlyList<TelemetryReadingResponse> Ingest(Guid[] ids) => category switch
        {
            SensorCategory.Environmental => service.IngestEnvironmentalBatch(ids
                .Select(id => new TelemetryPacket<float>(id, 22f, timestamp, "C")).ToArray()),
            SensorCategory.PowerConsumption => service.IngestPowerBatch(ids
                .Select(id => new TelemetryPacket<int>(id, 1000, timestamp, "W")).ToArray()),
            _ => service.IngestActuatorBatch(ids
                .Select(id => new TelemetryPacket<bool>(id, true, timestamp, "state")).ToArray())
        };

        Assert.Throws<KeyNotFoundException>(() => Ingest([sensor, Guid.NewGuid()]));
        Assert.Empty(service.GetHistory());
        Assert.Empty(service.GetHistoryRange(sensor, timestamp, timestamp).Readings);
        Assert.Null(repository.GetById(sensor)!.LastHeartbeat);

        var accepted = Assert.Single(Ingest([sensor]));

        Assert.Equal(accepted, Assert.Single(service.GetHistory()));
        Assert.Equal(accepted, Assert.Single(service.GetHistoryRange(sensor, timestamp, timestamp).Readings));
    }

    private static Guid AddSensor(
        InMemorySensorRepository repository,
        SensorCategory category)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = category.ToString(),
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1",
            Category = category
        };

        repository.Add(sensor);
        return sensor.Id;
    }
}
