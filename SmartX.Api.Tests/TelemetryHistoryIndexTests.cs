using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class TelemetryHistoryIndexTests
{
    [Fact]
    public void RangeSeparatesSensorsAndIncludesLateAndDuplicateReadings()
    {
        var (service, first, second) = CreateService();
        var start = DateTimeOffset.UtcNow;
        var newest = service.IngestEnvironmental(new(first, 23f, start.AddSeconds(2), "C"));
        service.IngestEnvironmental(new(second, 24f, start, "C"));
        var late = service.IngestEnvironmental(new(first, 21f, start, "C"));
        var duplicate = service.IngestEnvironmental(new(first, 22f, start, "C"));

        var range = service.GetHistoryRange(first, start, newest.Timestamp);

        Assert.Equal(new[] { late, duplicate, newest }, range.Readings);
        Assert.False(range.HasMore);
        var limited = service.GetHistoryRange(first, start, newest.Timestamp, 2);
        Assert.Equal(new[] { duplicate, newest }, limited.Readings);
        Assert.True(limited.HasMore);
        Assert.Equal(second, Assert.Single(service.GetHistoryRange(second, start, start).Readings).SensorId);
        Assert.Empty(service.GetHistoryRange(first, start.AddSeconds(3), start.AddSeconds(4)).Readings);
    }

    [Fact]
    public void RetentionRemovesExactArrivalsAcrossSensorsAndRecreatesAnEmptyIndex()
    {
        var (service, first, second) = CreateService();
        var start = DateTimeOffset.UtcNow;
        service.IngestEnvironmental(new(first, 21f, start, "C"));
        var retained = service.IngestEnvironmental(new(first, 22f, start, "C"));
        var batch = service.IngestEnvironmentalBatch(Enumerable.Range(0, 9999)
            .Select(_ => new TelemetryPacket<float>(second, 23f, start, "C"))
            .ToArray());

        Assert.Equal(retained, Assert.Single(service.GetHistoryRange(first, start, start).Readings));
        Assert.Equal(batch, service.GetHistoryRange(second, start, start, 10000).Readings);

        var delayed = service.IngestEnvironmental(new(second, 24f, start.AddDays(-1), "C"));
        var empty = service.GetHistoryRange(first, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Empty(empty.Readings);
        Assert.False(empty.HasMore);
        var full = service.GetHistoryRange(second, delayed.Timestamp, start, 10000);
        Assert.Equal(10000, full.Readings.Count);
        Assert.Equal(delayed, full.Readings[0]);
        Assert.False(full.HasMore);

        var returned = service.IngestEnvironmental(new(first, 25f, start, "C"));

        Assert.Equal(returned, Assert.Single(service.GetHistoryRange(first, start, start).Readings));
        var remaining = service.GetHistoryRange(second, delayed.Timestamp, start, 10000);
        Assert.Equal(9999, remaining.Readings.Count);
        Assert.DoesNotContain(batch[0], remaining.Readings);
        Assert.Equal(service.GetHistory(second, 1000), service.GetHistoryRange(second, delayed.Timestamp, start, 1000).Readings);
    }

    [Fact]
    public void ConcurrentIngestionAndQueriesPreserveEveryReading()
    {
        var (service, first, second) = CreateService();
        var start = DateTimeOffset.UtcNow;

        Parallel.For(0, 1000, value =>
        {
            var sensor = value % 2 == 0 ? first : second;
            service.IngestEnvironmental(new(sensor, 22f, start.AddMilliseconds(value), "C"));
            var snapshot = service.GetHistoryRange(sensor, start, start.AddSeconds(1));
            Assert.All(snapshot.Readings, reading => Assert.Equal(sensor, reading.SensorId));
            Assert.Equal(snapshot.Readings.OrderBy(reading => reading.Timestamp), snapshot.Readings);
        });

        foreach (var sensor in new[] { first, second })
        {
            var range = service.GetHistoryRange(sensor, start, start.AddSeconds(1), 1000);
            Assert.Equal(500, range.Readings.Count);
            Assert.False(range.HasMore);
            Assert.Equal(service.GetHistory(sensor, 1000), range.Readings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangeValidatesArgumentsWithOrWithoutAnIndex(bool ingest)
    {
        var (service, first, _) = CreateService();
        var start = DateTimeOffset.UtcNow;
        if (ingest)
        {
            service.IngestEnvironmental(new(first, 22f, start, "C"));
        }

        Assert.Throws<ArgumentException>(() => service.GetHistoryRange(first, start.AddSeconds(1), start));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetHistoryRange(first, start, start, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetHistoryRange(first, start, start, -1));
        var unknown = service.GetHistoryRange(Guid.NewGuid(), start, start);
        Assert.Empty(unknown.Readings);
        Assert.False(unknown.HasMore);
    }

    private static (TelemetryService Service, Guid First, Guid Second) CreateService()
    {
        var repository = new InMemorySensorRepository();
        var sensors = Enumerable.Range(1, 2).Select(number => new Sensor
        {
            DeviceIdentifier = $"ENV-{number}",
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = $"Node {number}",
            Category = SensorCategory.Environmental
        }).ToArray();

        foreach (var sensor in sensors)
        {
            repository.Add(sensor);
        }

        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        return (new TelemetryService(repository, issues, commands), sensors[0].Id, sensors[1].Id);
    }
}
