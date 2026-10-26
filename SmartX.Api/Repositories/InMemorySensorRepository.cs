using SmartX.Api.Models;

namespace SmartX.Api.Repositories;

public sealed class InMemorySensorRepository : ISensorRepository
{
    private readonly Dictionary<string, Sensor> _sensorsByIdentifier =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Sensor> _sensorsById = [];
    private readonly object _sync = new();

    public bool Add(Sensor sensor)
    {
        lock (_sync)
        {
            var identifier = sensor.DeviceIdentifier.Trim();

            if (_sensorsByIdentifier.ContainsKey(identifier) ||
                _sensorsById.ContainsKey(sensor.Id))
            {
                return false;
            }

            _sensorsByIdentifier.Add(identifier, sensor);
            _sensorsById.Add(sensor.Id, sensor);
            return true;
        }
    }

    public Sensor? GetById(Guid id)
    {
        lock (_sync)
        {
            return _sensorsById.GetValueOrDefault(id);
        }
    }

    public Sensor? GetByIdentifier(string deviceIdentifier)
    {
        lock (_sync)
        {
            return _sensorsByIdentifier.GetValueOrDefault(
                deviceIdentifier.Trim());
        }
    }

    public IReadOnlyList<Sensor> GetAll()
    {
        lock (_sync)
        {
            return _sensorsById.Values.ToList();
        }
    }

    public void UpdateHeartbeat(Guid id, DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            if (_sensorsById.TryGetValue(id, out var sensor) &&
                (sensor.LastHeartbeat is null || timestamp > sensor.LastHeartbeat))
            {
                sensor.LastHeartbeat = timestamp;
            }
        }
    }
}
