namespace SmartX.Web.Models;

public enum LearnedAction
{
    InspectDevice,
    TurnOn,
    TurnOff
}

public sealed record ActionSuggestionViewModel(
    Guid TargetSensorId,
    string DeviceIdentifier,
    LearnedAction Action,
    int EvidenceCount,
    DateTimeOffset LastObservedAt,
    string Reason);
