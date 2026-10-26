using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class DeviceCommandServiceTests
{
    private readonly InMemorySensorRepository _repository = new();
    private readonly DeviceCommandService _commands;
    private readonly TelemetryService _telemetry;

    public DeviceCommandServiceTests()
    {
        var issues = new ActiveIssueService();
        _commands = new DeviceCommandService(_repository, new UserActivityService(_repository, issues));
        _telemetry = new TelemetryService(_repository, issues, _commands);
    }

    [Fact]
    public void CommandsRecordPreviousStateAndChangeTheActuator()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, true, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));

        var off = _commands.Execute(sensor.Id, false);
        var on = _commands.Execute(sensor.Id, true);

        Assert.Equal(DeviceCommandOutcome.Succeeded, off.Outcome);
        Assert.True(off.PreviousState);
        Assert.False(off.RequestedState);
        Assert.Equal(DeviceCommandOutcome.Succeeded, on.Outcome);
        Assert.False(on.PreviousState);
        Assert.True(on.RequestedState);
        Assert.Equal(off.StateVersion + 1, on.StateVersion);
        Assert.True(on.ExecutedAt >= off.ExecutedAt);
        Assert.Equal(new[] { on.Id, off.Id }, _commands.GetHistory().Select(command => command.Id));

        var sensors = new SensorService(_repository, new DeploymentTreeValidator(), _commands);
        Assert.True(sensors.GetById(sensor.Id)!.ActuatorState!.IsOn);
        Assert.Equal(on.StateVersion, sensors.GetById(sensor.Id)!.ActuatorState!.Version);
    }

    [Fact]
    public void RepeatingCurrentStateDoesNotCreateAStateChange()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var before = _commands.GetState(sensor.Id);

        var command = _commands.Execute(sensor.Id, false);

        Assert.Equal(DeviceCommandOutcome.NoChange, command.Outcome);
        Assert.Equal(before, _commands.GetState(sensor.Id));
        Assert.Equal(command, Assert.Single(_commands.GetHistory()));
    }

    [Fact]
    public void UnknownDeviceAndNonActuatorAreRejected()
    {
        var sensor = AddSensor(SensorCategory.PowerConsumption);

        Assert.Equal(DeviceCommandOutcome.Rejected, _commands.Execute(Guid.NewGuid(), true).Outcome);
        Assert.Equal(DeviceCommandOutcome.Rejected, _commands.Execute(sensor.Id, true).Outcome);
        Assert.Null(_commands.GetState(sensor.Id));
        Assert.Equal(2, _commands.GetHistory().Count);
    }

    [Fact]
    public void ConnectedActuatorWithUnknownStateIsRejected()
    {
        var sensor = AddSensor();
        _repository.UpdateHeartbeat(sensor.Id, DateTimeOffset.UtcNow);

        var command = _commands.Execute(sensor.Id, true);

        Assert.Equal(DeviceCommandOutcome.Rejected, command.Outcome);
        Assert.Contains("unknown", command.Message);
        Assert.Null(command.PreviousState);
        Assert.Null(_commands.GetState(sensor.Id));
    }

    [Fact]
    public void DisconnectedActuatorIsRejectedWithoutChangingItsStateOrHeartbeat()
    {
        var sensor = AddSensor();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        _telemetry.IngestActuator(new(sensor.Id, false, timestamp, "state"));
        var before = _commands.GetState(sensor.Id);

        var command = _commands.Execute(sensor.Id, true);

        Assert.Equal(DeviceCommandOutcome.Rejected, command.Outcome);
        Assert.Contains("disconnected", command.Message);
        Assert.Equal(before, _commands.GetState(sensor.Id));
        Assert.Equal(timestamp, sensor.LastHeartbeat);
    }

    [Fact]
    public void OlderOrStaleVersionTelemetryCannotUndoANewerCommand()
    {
        var sensor = AddSensor();
        var initialTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        _telemetry.IngestActuator(new(sensor.Id, true, initialTime, "state"));
        var initialState = _commands.GetState(sensor.Id)!;
        var command = _commands.Execute(sensor.Id, false);

        _telemetry.IngestActuator(new(sensor.Id, true, initialTime, "state"));
        _telemetry.IngestActuator(new(sensor.Id, true, command.ExecutedAt.AddMilliseconds(1), "state", initialState.Version));

        Assert.False(_commands.GetState(sensor.Id)!.IsOn);
        Assert.Equal(command.StateVersion, _commands.GetState(sensor.Id)!.Version);

        _telemetry.IngestActuator(new(sensor.Id, true, command.ExecutedAt.AddMilliseconds(2), "state", command.StateVersion));

        Assert.True(_commands.GetState(sensor.Id)!.IsOn);
        Assert.Equal(command.StateVersion + 1, _commands.GetState(sensor.Id)!.Version);
    }

    [Fact]
    public void ConcurrentCommandsKeepAnAccuratePreviousStateChain()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));

        Parallel.For(0, 100, index => _commands.Execute(sensor.Id, index % 2 == 0));

        var expectedState = false;
        var history = _commands.GetHistory().Reverse().ToArray();
        Assert.Equal(100, history.Length);
        foreach (var command in history)
        {
            Assert.Equal(expectedState, command.PreviousState);
            Assert.NotEqual(DeviceCommandOutcome.Rejected, command.Outcome);
            expectedState = command.RequestedState;
        }
        Assert.Equal(expectedState, _commands.GetState(sensor.Id)!.IsOn);
    }

    [Fact]
    public void UndoRestoresSuccessiveCommandsInReverseOrder()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var first = _commands.Execute(sensor.Id, true);
        var second = _commands.Execute(sensor.Id, false);
        var third = _commands.Execute(sensor.Id, true);

        foreach (var command in new[] { third, second, first })
        {
            var undo = _commands.Undo();
            Assert.NotNull(undo);
            Assert.Equal(DeviceCommandOutcome.Succeeded, undo.Outcome);
            Assert.Equal(command.Id, undo.UndoesCommandId);
            Assert.Equal(command.PreviousState, _commands.GetState(sensor.Id)!.IsOn);
            Assert.Equal(undo, _commands.GetHistory()[0]);
        }

        Assert.False(_commands.GetState(sensor.Id)!.IsOn);
        Assert.True(_commands.GetState(sensor.Id)!.Version > third.StateVersion);
        Assert.Null(_commands.Undo());
    }

    [Fact]
    public void EmptyHistoryRejectedCommandsAndNoChangesDoNotCreateUndoEntries()
    {
        Assert.Null(_commands.Undo());
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        _commands.Execute(sensor.Id, false);
        _commands.Execute(Guid.NewGuid(), true);
        Assert.Null(_commands.Undo());

        var command = _commands.Execute(sensor.Id, true);
        _commands.Execute(sensor.Id, true);
        _commands.Execute(Guid.NewGuid(), false);

        Assert.Equal(command.Id, _commands.Undo()!.UndoesCommandId);
        Assert.Null(_commands.Undo());
    }

    [Fact]
    public void UndoMaintainsGlobalOrderAcrossDevices()
    {
        var firstSensor = AddSensor();
        var secondSensor = AddSensor();
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-1);
        _telemetry.IngestActuator(new(firstSensor.Id, false, timestamp, "state"));
        _telemetry.IngestActuator(new(secondSensor.Id, false, timestamp, "state"));
        var first = _commands.Execute(firstSensor.Id, true);
        var second = _commands.Execute(secondSensor.Id, true);
        var third = _commands.Execute(firstSensor.Id, false);

        foreach (var command in new[] { third, second, first })
        {
            var undo = _commands.Undo()!;
            Assert.Equal(DeviceCommandOutcome.Succeeded, undo.Outcome);
            Assert.Equal(command.Id, undo.UndoesCommandId);
        }

        Assert.False(_commands.GetState(firstSensor.Id)!.IsOn);
        Assert.False(_commands.GetState(secondSensor.Id)!.IsOn);
    }

    [Fact]
    public void DisconnectedUndoCanBeRetriedAfterReconnect()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var command = _commands.Execute(sensor.Id, true);
        var state = _commands.GetState(sensor.Id);
        _repository.GetById(sensor.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-1);

        var rejected = _commands.Undo()!;
        Assert.Equal(DeviceCommandOutcome.Rejected, rejected.Outcome);
        Assert.Equal(command.Id, rejected.UndoesCommandId);
        Assert.Equal(state, _commands.GetState(sensor.Id));

        _repository.UpdateHeartbeat(sensor.Id, DateTimeOffset.UtcNow);
        var restored = _commands.Undo()!;
        Assert.Equal(DeviceCommandOutcome.Succeeded, restored.Outcome);
        Assert.Equal(command.Id, restored.UndoesCommandId);
        Assert.Null(_commands.Undo());
    }

    [Fact]
    public void UndoRejectsInterveningChangesEvenIfTheValueChangesBack()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var command = _commands.Execute(sensor.Id, true);
        _telemetry.IngestActuator(new(sensor.Id, false, command.ExecutedAt.AddMilliseconds(1), "state"));
        _telemetry.IngestActuator(new(sensor.Id, true, command.ExecutedAt.AddMilliseconds(2), "state"));
        var state = _commands.GetState(sensor.Id);

        var rejected = _commands.Undo()!;

        Assert.Equal(DeviceCommandOutcome.Rejected, rejected.Outcome);
        Assert.Contains("newer state", rejected.Message);
        Assert.Equal(state, _commands.GetState(sensor.Id));
        Assert.Equal(command.Id, _commands.Undo()!.UndoesCommandId);
    }

    [Fact]
    public void UndoingANewerCommandDoesNotMakeAnOlderConflictedCommandSafe()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var first = _commands.Execute(sensor.Id, true);
        _telemetry.IngestActuator(new(sensor.Id, false, first.ExecutedAt.AddMilliseconds(1), "state"));
        var second = _commands.Execute(sensor.Id, true);

        Assert.Equal(second.Id, _commands.Undo()!.UndoesCommandId);
        var state = _commands.GetState(sensor.Id);
        var rejected = _commands.Undo()!;

        Assert.Equal(first.Id, rejected.UndoesCommandId);
        Assert.Equal(DeviceCommandOutcome.Rejected, rejected.Outcome);
        Assert.Equal(state, _commands.GetState(sensor.Id));
    }

    [Fact]
    public void HeartbeatsDoNotBlockUndoAndStaleReportsCannotReverseIt()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var command = _commands.Execute(sensor.Id, true);
        _telemetry.IngestActuator(new(sensor.Id, true, command.ExecutedAt.AddMilliseconds(1), "state", command.StateVersion));

        var undo = _commands.Undo()!;
        Assert.Equal(DeviceCommandOutcome.Succeeded, undo.Outcome);
        _telemetry.IngestActuator(new(sensor.Id, true, undo.ExecutedAt.AddSeconds(1), "state", command.StateVersion));

        Assert.False(_commands.GetState(sensor.Id)!.IsOn);
        Assert.Equal(undo.StateVersion, _commands.GetState(sensor.Id)!.Version);
    }

    [Fact]
    public void ConcurrentUndoRequestsRestoreEachCommandOnlyOnce()
    {
        var sensor = AddSensor();
        _telemetry.IngestActuator(new(sensor.Id, false, DateTimeOffset.UtcNow.AddSeconds(-1), "state"));
        var first = _commands.Execute(sensor.Id, true);
        var second = _commands.Execute(sensor.Id, false);
        var results = new System.Collections.Concurrent.ConcurrentBag<DeviceCommandResult>();

        Parallel.For(0, 10, _ =>
        {
            if (_commands.Undo() is { } undo)
            {
                results.Add(undo);
            }
        });

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(DeviceCommandOutcome.Succeeded, result.Outcome));
        Assert.Contains(results, result => result.UndoesCommandId == first.Id);
        Assert.Contains(results, result => result.UndoesCommandId == second.Id);
        Assert.False(_commands.GetState(sensor.Id)!.IsOn);
    }

    private Sensor AddSensor(SensorCategory category = SensorCategory.Actuator)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = Guid.NewGuid().ToString(),
            Facility = "Facility A", Zone = "Zone 1", Node = "Node 1", Category = category
        };
        _repository.Add(sensor);
        return sensor;
    }
}
