namespace SmartX.Api.Models;

public enum UserActivityKind
{
    Search,
    Command,
    Undo
}

public sealed record ActivityCondition(Guid SensorId, string IssueType, string Message);

public sealed record UserActivity(
    Guid Id,
    UserActivityKind Kind,
    DateTimeOffset Timestamp,
    string? Search,
    SensorCategory? Category,
    CommandStreamAlertFilter Alert,
    IReadOnlyList<Guid> MatchedSensorIds,
    DeviceCommandResult? Command,
    IReadOnlyList<ActivityCondition> Conditions);
