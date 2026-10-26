namespace SmartX.Api.Models;

public enum LearnedAction
{
    InspectDevice,
    TurnOn,
    TurnOff
}

public sealed record ActionPattern(
    ActivityCondition Condition,
    Guid TargetSensorId,
    LearnedAction Action,
    int EvidenceCount,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt);
