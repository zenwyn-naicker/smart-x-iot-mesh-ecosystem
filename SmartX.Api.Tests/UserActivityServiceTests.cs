using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class UserActivityServiceTests
{
    [Fact]
    public void HistoryKeepsTheLatestThousandActivities()
    {
        var service = new UserActivityService(new InMemorySensorRepository(), new ActiveIssueService());
        for (var index = 0; index < 1005; index++)
        {
            service.RecordSearch(new CommandStreamQuery { Search = $"Device {index}" }, []);
        }

        var history = service.GetHistory();
        Assert.Equal(1000, history.Count);
        Assert.Equal("Device 5", history[0].Search);
        Assert.Equal("Device 1004", history[^1].Search);
    }

    [Fact]
    public void SearchHistoryCopiesTheQueryAndMatchedDevices()
    {
        var service = new UserActivityService(new InMemorySensorRepository(), new ActiveIssueService());
        var sensorId = Guid.NewGuid();
        var query = new CommandStreamQuery { Search = "Pump", Category = SensorCategory.Actuator };
        var ids = new List<Guid> { sensorId, sensorId };
        service.RecordSearch(query, ids);
        query.Search = "Changed";
        ids.Clear();

        var recorded = Assert.Single(service.GetHistory());
        Assert.Equal("Pump", recorded.Search);
        Assert.Equal(sensorId, Assert.Single(recorded.MatchedSensorIds));
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)recorded.MatchedSensorIds).Clear());
    }

    [Fact]
    public void ConcurrentSearchesAreRecordedOnceEach()
    {
        var service = new UserActivityService(new InMemorySensorRepository(), new ActiveIssueService());
        Parallel.For(0, 100, index => service.RecordSearch(
            new CommandStreamQuery { Search = $"Device {index}" }, []));

        var history = service.GetHistory();
        Assert.Equal(100, history.Count);
        Assert.Equal(100, history.Select(activity => activity.Id).Distinct().Count());
        Assert.Equal(100, history.Select(activity => activity.Search).Distinct().Count());
    }
}
