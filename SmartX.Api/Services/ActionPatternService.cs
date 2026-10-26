using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed class ActionPatternService(UserActivityService activityService)
{
    public IReadOnlyList<ActionPattern> GetPatterns()
    {
        var history = activityService.GetHistory();
        var undoneCommands = history
            .Where(activity => activity.Kind == UserActivityKind.Undo &&
                activity.Command?.Outcome == DeviceCommandOutcome.Succeeded)
            .Select(activity => activity.Command!.UndoesCommandId!.Value)
            .ToHashSet();
        var patterns = new Dictionary<(ActivityCondition Condition, Guid Target, LearnedAction Action), ActionPattern>();

        foreach (var activity in history.DistinctBy(activity => activity.Id))
        {
            Guid target;
            LearnedAction action;
            if (activity.Kind == UserActivityKind.Command &&
                activity.Command is { Outcome: DeviceCommandOutcome.Succeeded } command &&
                !undoneCommands.Contains(command.Id))
            {
                target = command.SensorId;
                action = command.RequestedState ? LearnedAction.TurnOn : LearnedAction.TurnOff;
            }
            else if (activity.Kind == UserActivityKind.Search &&
                !string.IsNullOrWhiteSpace(activity.Search) && activity.MatchedSensorIds.Count == 1)
            {
                target = activity.MatchedSensorIds[0];
                action = LearnedAction.InspectDevice;
            }
            else
            {
                continue;
            }

            foreach (var condition in activity.Conditions.Distinct())
            {
                var key = (condition, target, action);
                if (patterns.TryGetValue(key, out var previous))
                {
                    patterns[key] = previous with
                    {
                        EvidenceCount = previous.EvidenceCount + 1,
                        FirstObservedAt = activity.Timestamp < previous.FirstObservedAt
                            ? activity.Timestamp : previous.FirstObservedAt,
                        LastObservedAt = activity.Timestamp > previous.LastObservedAt
                            ? activity.Timestamp : previous.LastObservedAt
                    };
                }
                else
                {
                    patterns.Add(key, new ActionPattern(condition, target, action, 1,
                        activity.Timestamp, activity.Timestamp));
                }
            }
        }

        return patterns.Values
            .Where(pattern => pattern.EvidenceCount >= 2)
            .OrderBy(pattern => pattern.Condition.SensorId)
            .ThenBy(pattern => pattern.Condition.IssueType, StringComparer.Ordinal)
            .ThenBy(pattern => pattern.Condition.Message, StringComparer.Ordinal)
            .ThenBy(pattern => pattern.TargetSensorId)
            .ThenBy(pattern => pattern.Action)
            .ToArray();
    }
}
