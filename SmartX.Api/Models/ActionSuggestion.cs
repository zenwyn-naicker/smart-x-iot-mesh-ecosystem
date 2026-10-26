namespace SmartX.Api.Models;

public sealed record ActionSuggestion(
    Guid TargetSensorId,
    string DeviceIdentifier,
    LearnedAction Action,
    ActivityCondition Condition,
    int EvidenceCount,
    DateTimeOffset LastObservedAt,
    string Reason);
