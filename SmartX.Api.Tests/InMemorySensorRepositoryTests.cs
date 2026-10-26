using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Tests;

public sealed class InMemorySensorRepositoryTests
{
    [Theory]
    [InlineData("ESP32-ENV-01")]
    [InlineData("esp32-env-01")]
    [InlineData(" ESP32-ENV-01 ")]
    public void IdentifierLookupAndDuplicateCheckUseNormalizedKeys(
        string identifier)
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor(" ESP32-ENV-01 ");
        var duplicate = CreateSensor(identifier);

        Assert.True(repository.Add(sensor));
        Assert.Same(sensor, repository.GetByIdentifier(identifier));
        Assert.Same(sensor, repository.GetById(sensor.Id));
        Assert.False(repository.Add(duplicate));
        Assert.Null(repository.GetById(duplicate.Id));
        Assert.Single(repository.GetAll());
    }

    [Fact]
    public void DuplicateIdDoesNotLeaveAnIdentifierEntry()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor("ESP32-ENV-01");
        var duplicate = CreateSensor("ESP32-ENV-02", sensor.Id);

        Assert.True(repository.Add(sensor));
        Assert.False(repository.Add(duplicate));
        Assert.Null(repository.GetByIdentifier(duplicate.DeviceIdentifier));
        Assert.Same(sensor, repository.GetById(sensor.Id));
        Assert.Single(repository.GetAll());
    }

    [Fact]
    public void HeartbeatUpdateIsVisibleThroughBothLookups()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor("ESP32-ENV-01");
        var timestamp = DateTimeOffset.UtcNow;
        repository.Add(sensor);

        repository.UpdateHeartbeat(sensor.Id, timestamp);

        Assert.Equal(timestamp, repository.GetById(sensor.Id)!.LastHeartbeat);
        Assert.Equal(timestamp,
            repository.GetByIdentifier(sensor.DeviceIdentifier)!.LastHeartbeat);
    }

    [Fact]
    public void UnknownDeviceLookupsAndHeartbeatAreHandled()
    {
        var repository = new InMemorySensorRepository();
        var id = Guid.NewGuid();

        repository.UpdateHeartbeat(id, DateTimeOffset.UtcNow);

        Assert.Null(repository.GetById(id));
        Assert.Null(repository.GetByIdentifier("UNKNOWN"));
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public void ConcurrentHeartbeatUpdatesKeepTheNewestTimestamp()
    {
        var repository = new InMemorySensorRepository();
        var sensor = CreateSensor("ESP32-ENV-01");
        repository.Add(sensor);
        var start = DateTimeOffset.UtcNow;

        Parallel.For(0, 100, index =>
            repository.UpdateHeartbeat(sensor.Id, start.AddMilliseconds(index)));
        repository.UpdateHeartbeat(sensor.Id, start);

        Assert.Equal(start.AddMilliseconds(99), repository.GetById(sensor.Id)!.LastHeartbeat);
    }

    private static Sensor CreateSensor(string identifier, Guid? id = null)
    {
        return new Sensor
        {
            Id = id ?? Guid.NewGuid(),
            DeviceIdentifier = identifier,
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 7",
            Category = SensorCategory.Environmental
        };
    }
}
