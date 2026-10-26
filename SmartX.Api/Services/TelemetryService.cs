using System.Globalization;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class TelemetryService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly ActiveIssueService _activeIssueService;
    private readonly DeviceCommandService _commandService;
    private readonly object _sync = new();
    private readonly PowerTelemetryClassifier _powerClassifier;

    private readonly TelemetryBatchBuffer<TelemetryPacket<float>>
        _environmentalBuffer = new(100);

    private readonly TelemetryBatchBuffer<TelemetryPacket<int>>
        _powerBuffer = new(100);

    private readonly TelemetryBatchBuffer<TelemetryPacket<bool>>
        _actuatorBuffer = new(100);

    private const int MaximumHistoryReadings = 10000;
    private readonly SortedDictionary<(DateTimeOffset Timestamp, long Sequence),
        TelemetryReadingResponse> _history = [];
    private readonly Queue<(DateTimeOffset Timestamp, long Sequence)>
        _historyArrivalOrder = [];
    private readonly Dictionary<Guid, TelemetryHistoryTree> _historyIndexes = [];
    private long _historySequence;

    private readonly Dictionary<Guid, PowerSnapshot> _latestPower = [];
    private readonly Dictionary<Guid, DateTimeOffset> _latestTelemetry = [];

    public TelemetryService(
        ISensorRepository sensorRepository,
        ActiveIssueService activeIssueService,
        DeviceCommandService commandService,
        PowerTelemetryClassifier? powerClassifier = null)
    {
        _sensorRepository = sensorRepository;
        _activeIssueService = activeIssueService;
        _commandService = commandService;
        _powerClassifier = powerClassifier ?? new PowerTelemetryClassifier();
    }

    public TelemetryReadingResponse IngestEnvironmental(
        TelemetryPacket<float> packet)
    {
        return IngestEnvironmentalBatch([packet])[0];
    }

    public TelemetryReadingResponse IngestPower(
        TelemetryPacket<int> packet)
    {
        return IngestPowerBatch([packet])[0];
    }

    public TelemetryReadingResponse IngestActuator(
        TelemetryPacket<bool> packet)
    {
        return IngestActuatorBatch([packet])[0];
    }

    internal TelemetryReadingResponse IngestQueuedPower(TelemetryWorkItem.Power item)
    {
        return IngestPowerBatch([item.Packet], item.HasPowerChange)[0];
    }

    public IReadOnlyList<TelemetryReadingResponse>
        IngestEnvironmentalBatch(
            TelemetryPacket<float>[] packets)
    {
        var sensors = packets
            .Select(packet => EnsureSensor(
                packet.SensorId,
                SensorCategory.Environmental))
            .ToList();

        var batch = ConvertBatch(_environmentalBuffer, packets);
        var responses = batch
            .Select(packet =>
            {
                var anomaly = TelemetryAlertPolicy.IsEnvironmentalCritical(packet.Value);

                return new TelemetryReadingResponse(
                    Guid.NewGuid(),
                    packet.SensorId,
                    "Environmental",
                    packet.Value.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture),
                    packet.Unit,
                    packet.Timestamp,
                    anomaly,
                    anomaly
                        ? "Environmental reading is outside the expected range."
                        : null,
                    TelemetryAlertPolicy.MinimumEnvironmentalValue,
                    TelemetryAlertPolicy.MaximumEnvironmentalValue);
            })
            .ToList();

        lock (_sync)
        {
            foreach (var response in responses)
            {
                StoreReadingLocked(response, sensors);
            }
        }

        UpdateHeartbeats(batch);

        return responses;
    }

    public IReadOnlyList<TelemetryReadingResponse>
        IngestPowerBatch(
            TelemetryPacket<int>[] packets)
    {
        return IngestPowerBatch(packets, null);
    }

    private IReadOnlyList<TelemetryReadingResponse> IngestPowerBatch(
        TelemetryPacket<int>[] packets,
        bool? powerChange)
    {
        var sensors = packets
            .Select(packet => EnsureSensor(
                packet.SensorId,
                SensorCategory.PowerConsumption))
            .ToList();

        var batch = ConvertBatch(_powerBuffer, packets);
        var responses = new List<TelemetryReadingResponse>(batch.Count);

        lock (_sync)
        {
            foreach (var packet in batch)
            {
                var current = new PowerReading(packet.Value);

                var previous = _latestPower.GetValueOrDefault(packet.SensorId);

                var largeDelta = powerChange ?? _powerClassifier.ClassifyAndTrack(packet);

                var anomaly = TelemetryAlertPolicy.IsPowerCritical(current.Watts) || largeDelta;

                var message = largeDelta
                    ? "Power load changed by more than 1500 watts."
                    : anomaly
                        ? "Power load is outside the expected range."
                        : null;

                var response = new TelemetryReadingResponse(
                    Guid.NewGuid(),
                    packet.SensorId,
                    "PowerConsumption",
                    packet.Value.ToString(
                        CultureInfo.InvariantCulture),
                    packet.Unit,
                    packet.Timestamp,
                    anomaly,
                    message,
                    TelemetryAlertPolicy.MinimumPowerWatts,
                    TelemetryAlertPolicy.MaximumPowerWatts);

                responses.Add(response);
                if (previous is null || packet.Timestamp > previous.Timestamp)
                {
                    _latestPower[packet.SensorId] = new PowerSnapshot(
                        packet.SensorId,
                        current,
                        packet.Timestamp);
                }

                StoreReadingLocked(response, sensors);
            }
        }

        UpdateHeartbeats(batch);

        return responses;
    }

    public IReadOnlyList<TelemetryReadingResponse>
        IngestActuatorBatch(
            TelemetryPacket<bool>[] packets)
    {
        var sensors = packets
            .Select(packet => EnsureSensor(
                packet.SensorId,
                SensorCategory.Actuator))
            .ToList();

        var batch = ConvertBatch(_actuatorBuffer, packets);
        var responses = batch
            .Select(packet =>
                new TelemetryReadingResponse(
                    Guid.NewGuid(),
                    packet.SensorId,
                    "Actuator",
                    packet.Value ? "On" : "Off",
                    packet.Unit,
                    packet.Timestamp,
                    false,
                    null,
                    null,
                    null))
            .ToList();

        lock (_sync)
        {
            foreach (var packet in batch)
            {
                _commandService.UpdateFromTelemetry(packet);
            }

            foreach (var response in responses)
            {
                StoreReadingLocked(response, sensors);
            }
        }

        UpdateHeartbeats(batch);

        return responses;
    }

    public IReadOnlyList<TelemetryReadingResponse> GetHistory(
        Guid? sensorId = null,
        int limit = 200,
        IReadOnlySet<Guid>? sensorIds = null)
    {
        lock (_sync)
        {
            if (sensorId is { } id)
            {
                if (sensorIds is not null && !sensorIds.Contains(id))
                {
                    return [];
                }

                return _historyIndexes.TryGetValue(id, out var index)
                    ? index.GetRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                        Math.Clamp(limit, 1, 1000)).Readings
                    : [];
            }

            return _history.Values
                .Where(reading => sensorIds is null || sensorIds.Contains(reading.SensorId))
                .TakeLast(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
    }

    public TelemetryHistoryRangeResponse GetHistoryRange(
        Guid sensorId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 200)
    {
        if (from > to)
        {
            throw new ArgumentException("The start time must not be after the end time.", nameof(from));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        lock (_sync)
        {
            return _historyIndexes.TryGetValue(sensorId, out var index)
                ? index.GetRange(from, to, limit)
                : new TelemetryHistoryRangeResponse([], false);
        }
    }

    public IReadOnlyList<ActiveIssue> GetActiveIssues()
    {
        return _activeIssueService.GetActiveIssues();
    }

    public PowerReading GetAggregatePower()
    {
        lock (_sync)
        {
            var total = new PowerReading(0);

            foreach (var snapshot in _latestPower.Values)
            {
                total += snapshot.Reading;
            }

            return total;
        }
    }

    private Sensor EnsureSensor(
        Guid sensorId,
        SensorCategory expectedCategory)
    {
        var sensor = _sensorRepository.GetById(sensorId)
            ?? throw new KeyNotFoundException(
                "The selected sensor does not exist.");

        if (sensor.Category != expectedCategory)
        {
            throw new InvalidOperationException(
                $"This endpoint only accepts {expectedCategory} sensors.");
        }

        return sensor;
    }

    private List<TelemetryPacket<T>> ConvertBatch<T>(
        TelemetryBatchBuffer<TelemetryPacket<T>> buffer,
        TelemetryPacket<T>[] packets)
    {
        lock (_sync)
        {
            buffer.AddBatch(packets);
            return buffer.DrainToOptimizedList();
        }
    }

    private void StoreReadingLocked(
        TelemetryReadingResponse response,
        IReadOnlyList<Sensor> sensors)
    {
        var key = (Timestamp: response.Timestamp, Sequence: _historySequence++);
        _history.Add(key, response);
        _historyArrivalOrder.Enqueue(key);

        if (!_historyIndexes.TryGetValue(response.SensorId, out var index))
        {
            index = new TelemetryHistoryTree();
            _historyIndexes.Add(response.SensorId, index);
        }

        index.Add(key.Sequence, response);

        if (_history.Count > MaximumHistoryReadings)
        {
            var expiredKey = _historyArrivalOrder.Dequeue();
            var expiredReading = _history[expiredKey];
            _history.Remove(expiredKey);

            var expiredIndex = _historyIndexes[expiredReading.SensorId];
            expiredIndex.Remove(expiredKey.Timestamp, expiredKey.Sequence);

            if (expiredIndex.Count == 0)
            {
                _historyIndexes.Remove(expiredReading.SensorId);
            }
        }

        if (_latestTelemetry.TryGetValue(response.SensorId, out var latest) &&
            response.Timestamp <= latest)
        {
            return;
        }

        _latestTelemetry[response.SensorId] = response.Timestamp;

        var sensor = sensors.First(item =>
            item.Id == response.SensorId);

        if (response.IsAnomaly)
        {
            _activeIssueService.Record(
                sensor,
                "Telemetry",
                response.AnomalyMessage
                    ?? "An anomalous reading was detected.",
                response.Timestamp);
        }
        else
        {
            _activeIssueService.Resolve(response.SensorId, "Telemetry");
        }
    }

    private void UpdateHeartbeats<T>(
        IEnumerable<TelemetryPacket<T>> packets)
    {
        foreach (var packet in packets)
        {
            _sensorRepository.UpdateHeartbeat(
                packet.SensorId,
                packet.Timestamp);

            if (DateTimeOffset.UtcNow - packet.Timestamp <= TimeSpan.FromSeconds(30))
            {
                _activeIssueService.Resolve(packet.SensorId, "Disconnect");
            }
        }
    }
}
