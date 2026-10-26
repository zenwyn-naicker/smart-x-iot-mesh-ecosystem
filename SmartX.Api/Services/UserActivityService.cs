using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class UserActivityService(ISensorRepository sensors, ActiveIssueService issues)
{
    private readonly Queue<UserActivity> _history = [];
    private readonly object _sync = new();

    public void RecordSearch(CommandStreamQuery query, IEnumerable<Guid> matchedSensorIds)
    {
        var search = query.Search?.Trim();
        if (string.IsNullOrEmpty(search) && query.Category is null && query.Alert == CommandStreamAlertFilter.All)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Add(new UserActivity(
            Guid.NewGuid(), UserActivityKind.Search, now,
            string.IsNullOrEmpty(search) ? null : search, query.Category, query.Alert,
            Array.AsReadOnly(matchedSensorIds.Distinct().ToArray()), null, CaptureConditions(now)));
    }

    public void RecordCommand(DeviceCommandResult command)
    {
        Add(new UserActivity(
            command.Id,
            command.UndoesCommandId is null ? UserActivityKind.Command : UserActivityKind.Undo,
            command.ExecutedAt, null, null, CommandStreamAlertFilter.All,
            Array.AsReadOnly(new[] { command.SensorId }), command, CaptureConditions(command.ExecutedAt)));
    }

    public IReadOnlyList<UserActivity> GetHistory()
    {
        lock (_sync)
        {
            return _history.ToArray();
        }
    }

    private IReadOnlyList<ActivityCondition> CaptureConditions(DateTimeOffset now)
    {
        var conditions = issues.GetActiveIssues()
            .Where(issue => issue.IssueType != "Disconnect")
            .Select(issue => new ActivityCondition(issue.SensorId, issue.IssueType, issue.Message))
            .ToList();
        foreach (var sensor in sensors.GetAll())
        {
            if (sensor.LastHeartbeat is null || now - sensor.LastHeartbeat > TimeSpan.FromSeconds(30))
            {
                conditions.Add(new ActivityCondition(sensor.Id, "Disconnect",
                    sensor.LastHeartbeat is null
                        ? "No heartbeat has been received."
                        : "The sensor heartbeat is overdue."));
            }
        }

        return conditions.AsReadOnly();
    }

    private void Add(UserActivity activity)
    {
        lock (_sync)
        {
            _history.Enqueue(activity);
            if (_history.Count > 1000)
            {
                _history.Dequeue();
            }
        }
    }
}
