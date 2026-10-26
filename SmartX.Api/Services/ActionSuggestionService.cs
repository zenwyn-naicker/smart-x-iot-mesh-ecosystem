using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed class ActionSuggestionService(
    ActionPatternService patterns,
    DashboardService dashboardService,
    DeviceCommandService commands)
{
    public IReadOnlyList<ActionSuggestion> GetSuggestions(int limit = 5)
    {
        var dashboard = dashboardService.GetDashboard();
        var sensors = dashboard.Sensors.ToDictionary(sensor => sensor.Id);
        var conditions = dashboard.ActiveIssues
            .Where(issue => issue.IssueType == "Disconnect" ||
                sensors.TryGetValue(issue.SensorId, out var sensor) && sensor.Status != SensorStatus.Disconnected)
            .Select(issue => new ActivityCondition(issue.SensorId, issue.IssueType, issue.Message))
            .ToHashSet();
        List<ActionSuggestion> suggestions = [];

        foreach (var pattern in patterns.GetPatterns())
        {
            if (!conditions.Contains(pattern.Condition) ||
                !sensors.TryGetValue(pattern.TargetSensorId, out var target) ||
                !sensors.TryGetValue(pattern.Condition.SensorId, out var source))
            {
                continue;
            }

            if (pattern.Action != LearnedAction.InspectDevice)
            {
                var state = commands.GetState(target.Id);
                if (target.Category != SensorCategory.Actuator || target.Status == SensorStatus.Disconnected ||
                    state is null || state.IsOn == (pattern.Action == LearnedAction.TurnOn))
                {
                    continue;
                }
            }

            var action = pattern.Action switch
            {
                LearnedAction.TurnOn => "turned this device on",
                LearnedAction.TurnOff => "turned this device off",
                _ => "searched for this device"
            };
            suggestions.Add(new ActionSuggestion(
                target.Id, target.DeviceIdentifier, pattern.Action, pattern.Condition,
                pattern.EvidenceCount, pattern.LastObservedAt,
                $"{source.DeviceIdentifier}: {pattern.Condition.Message} You {action} " +
                $"{pattern.EvidenceCount} times while this condition was active."));
        }

        return suggestions
            .OrderByDescending(suggestion => suggestion.EvidenceCount)
            .ThenByDescending(suggestion => suggestion.LastObservedAt)
            .ThenBy(suggestion => suggestion.TargetSensorId)
            .ThenBy(suggestion => suggestion.Action)
            .ThenBy(suggestion => suggestion.Condition.SensorId)
            .ThenBy(suggestion => suggestion.Condition.IssueType, StringComparer.Ordinal)
            .ThenBy(suggestion => suggestion.Condition.Message, StringComparer.Ordinal)
            .DistinctBy(suggestion => (suggestion.TargetSensorId, suggestion.Action))
            .Take(Math.Clamp(limit, 1, 20))
            .ToArray();
    }
}
