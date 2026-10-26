namespace SmartX.Api.Models;

public enum DeviceCommandOutcome
{
    Succeeded,
    NoChange,
    Rejected
}

public sealed record ActuatorState(bool IsOn, DateTimeOffset UpdatedAt, long Version);

public sealed record DeviceCommandResult(
    Guid Id,
    Guid SensorId,
    bool? PreviousState,
    bool RequestedState,
    DateTimeOffset ExecutedAt,
    DeviceCommandOutcome Outcome,
    string Message,
    long? StateVersion,
    Guid? UndoesCommandId = null);
