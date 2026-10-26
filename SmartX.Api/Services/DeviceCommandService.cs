using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class DeviceCommandService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly UserActivityService _activityService;
    private readonly Dictionary<Guid, ActuatorState> _states = [];
    private readonly List<DeviceCommandResult> _history = [];
    private readonly Dictionary<Guid, (Guid? SensorId, bool? IsOn, DeviceCommandResult? Result)> _requests = [];
    private Stack<UndoEntry> _undoStack = [];
    private readonly object _sync = new();

    public DeviceCommandService(ISensorRepository sensorRepository, UserActivityService activityService)
    {
        _sensorRepository = sensorRepository;
        _activityService = activityService;
    }

    public ActuatorState? GetState(Guid sensorId)
    {
        lock (_sync)
        {
            return _states.GetValueOrDefault(sensorId);
        }
    }

    internal void UpdateFromTelemetry(TelemetryPacket<bool> packet)
    {
        lock (_sync)
        {
            var previous = _states.GetValueOrDefault(packet.SensorId);
            if (previous is not null &&
                (packet.Timestamp <= previous.UpdatedAt ||
                 packet.StateVersion is long version && version != previous.Version))
            {
                return;
            }

            var nextVersion = previous is null ? 1 :
                previous.IsOn == packet.Value ? previous.Version : previous.Version + 1;
            _states[packet.SensorId] = new ActuatorState(packet.Value, packet.Timestamp, nextVersion);
        }
    }

    public DeviceCommandResult Execute(Guid sensorId, bool isOn)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var previous = _states.GetValueOrDefault(sensorId);
            var rejection = GetRejection(sensorId, previous, now);

            var outcome = rejection is not null ? DeviceCommandOutcome.Rejected :
                previous!.IsOn == isOn ? DeviceCommandOutcome.NoChange : DeviceCommandOutcome.Succeeded;

            if (outcome == DeviceCommandOutcome.Succeeded)
            {
                _states[sensorId] = new ActuatorState(isOn, now, previous!.Version + 1);
            }

            var result = new DeviceCommandResult(
                Guid.NewGuid(), sensorId, previous?.IsOn, isOn, now, outcome,
                rejection ?? (outcome == DeviceCommandOutcome.NoChange
                    ? "The actuator is already in the requested state."
                    : "The actuator state was updated."),
                _states.GetValueOrDefault(sensorId)?.Version);
            if (outcome == DeviceCommandOutcome.Succeeded)
            {
                _undoStack.Push(new UndoEntry(result, previous!.Version));
                if (_undoStack.Count > 1000)
                {
                    _undoStack = new Stack<UndoEntry>(_undoStack.Take(1000).Reverse());
                }
            }

            AddHistory(result);
            return result;
        }
    }

    public DeviceCommandResult Execute(Guid requestId, Guid sensorId, bool isOn)
    {
        return RunOnce(requestId, sensorId, isOn, () => Execute(sensorId, isOn))!;
    }

    public DeviceCommandResult? Undo(Guid requestId)
    {
        return RunOnce(requestId, null, null, Undo);
    }

    private DeviceCommandResult? RunOnce(
        Guid requestId, Guid? sensorId, bool? isOn, Func<DeviceCommandResult?> action)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.");
        }

        lock (_sync)
        {
            if (_requests.TryGetValue(requestId, out var previous))
            {
                if (previous.SensorId != sensorId || previous.IsOn != isOn)
                {
                    throw new InvalidOperationException("This request ID was already used for a different action.");
                }

                return previous.Result;
            }

            if (_requests.Count >= 10000)
            {
                throw new InvalidOperationException("The command request capacity for this session has been reached.");
            }

            var result = action();
            _requests.Add(requestId, (sensorId, isOn, result));
            return result;
        }
    }

    public DeviceCommandResult? Undo()
    {
        lock (_sync)
        {
            if (!_undoStack.TryPeek(out var entry))
            {
                return null;
            }

            var command = entry.Command;
            var now = DateTimeOffset.UtcNow;
            var current = _states.GetValueOrDefault(command.SensorId);
            var rejection = GetRejection(command.SensorId, current, now);
            if (rejection is null &&
                (current!.Version != entry.ExpectedVersion || current.IsOn != command.RequestedState))
            {
                rejection = "The actuator state changed after this command. Undo cannot overwrite a newer state.";
            }

            if (rejection is null)
            {
                var restored = new ActuatorState(command.PreviousState!.Value, now, current!.Version + 1);
                _states[command.SensorId] = restored;
                _undoStack.Pop();

                var earlier = _undoStack.FirstOrDefault(item => item.Command.SensorId == command.SensorId);
                if (earlier is not null && earlier.ExpectedVersion == entry.PreviousVersion)
                {
                    earlier.ExpectedVersion = restored.Version;
                }
            }

            var result = new DeviceCommandResult(
                Guid.NewGuid(), command.SensorId, current?.IsOn, command.PreviousState!.Value,
                now, rejection is null ? DeviceCommandOutcome.Succeeded : DeviceCommandOutcome.Rejected,
                rejection ?? "The previous actuator state was restored.",
                _states.GetValueOrDefault(command.SensorId)?.Version, command.Id);
            AddHistory(result);
            return result;
        }
    }

    private string? GetRejection(Guid sensorId, ActuatorState? state, DateTimeOffset now)
    {
        var sensor = _sensorRepository.GetById(sensorId);
        return sensor is null
            ? "The selected sensor does not exist."
            : sensor.Category != SensorCategory.Actuator
                ? "Only actuators support On/Off commands."
                : sensor.LastHeartbeat is null || now - sensor.LastHeartbeat > TimeSpan.FromSeconds(30)
                    ? "The actuator is disconnected."
                    : state is null
                        ? "The actuator state is unknown. Submit telemetry first."
                        : null;
    }

    private void AddHistory(DeviceCommandResult result)
    {
        _activityService.RecordCommand(result);
        _history.Add(result);
        if (_history.Count > 1000)
        {
            _history.RemoveAt(0);
        }
    }

    public IReadOnlyList<DeviceCommandResult> GetHistory(int limit = 100)
    {
        lock (_sync)
        {
            return _history.TakeLast(Math.Clamp(limit, 1, 1000)).Reverse().ToArray();
        }
    }

    private sealed class UndoEntry(DeviceCommandResult command, long previousVersion)
    {
        public DeviceCommandResult Command { get; } = command;
        public long PreviousVersion { get; } = previousVersion;
        public long ExpectedVersion { get; set; } = command.StateVersion!.Value;
    }
}
