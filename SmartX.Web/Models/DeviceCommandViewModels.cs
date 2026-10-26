using System.ComponentModel.DataAnnotations;

namespace SmartX.Web.Models;

public enum DeviceCommandOutcome
{
    Succeeded,
    NoChange,
    Rejected
}

public sealed record DeviceCommandViewModel(
    Guid Id, Guid SensorId, bool? PreviousState, bool RequestedState,
    DateTimeOffset ExecutedAt, DeviceCommandOutcome Outcome, string Message,
    long? StateVersion, Guid? UndoesCommandId);

public sealed record DeviceCommandForm(Guid RequestId, Guid SensorId, [Required] bool? IsOn);

public sealed record UndoCommandForm(Guid RequestId);

public sealed record CommandFeedback(bool Succeeded, string Message);
