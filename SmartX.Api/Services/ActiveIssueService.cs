using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed class ActiveIssueService
{
    private readonly HashSet<(Guid SensorId, string IssueType)> _activeKeys = [];
    private readonly Dictionary<(Guid SensorId, string IssueType), ActiveIssue> _issues = [];
    private readonly object _sync = new();

    public void Record(
        Sensor sensor,
        string issueType,
        string message,
        DateTimeOffset timestamp,
        bool countOccurrence = true)
    {
        lock (_sync)
        {
            var key = (sensor.Id, issueType);

            if (_activeKeys.Add(key))
            {
                _issues.Add(key, new ActiveIssue
                {
                    SensorId = sensor.Id,
                    DeviceIdentifier = sensor.DeviceIdentifier,
                    Severity = "Critical",
                    IssueType = issueType,
                    Message = message,
                    FirstSeen = timestamp,
                    LastSeen = timestamp
                });
                return;
            }

            var issue = _issues[key];

            if (timestamp >= issue.LastSeen)
            {
                issue.LastSeen = timestamp;
                issue.Message = message;
            }

            if (countOccurrence)
            {
                issue.Occurrences++;
            }
        }
    }

    public void Resolve(Guid sensorId, string issueType)
    {
        lock (_sync)
        {
            var key = (sensorId, issueType);

            if (_activeKeys.Remove(key))
            {
                _issues.Remove(key);
            }
        }
    }

    public IReadOnlyList<ActiveIssue> GetActiveIssues()
    {
        lock (_sync)
        {
            return _issues.Values
                .Select(issue => new ActiveIssue
                {
                    Id = issue.Id,
                    SensorId = issue.SensorId,
                    DeviceIdentifier = issue.DeviceIdentifier,
                    Severity = issue.Severity,
                    IssueType = issue.IssueType,
                    Message = issue.Message,
                    FirstSeen = issue.FirstSeen,
                    LastSeen = issue.LastSeen,
                    Occurrences = issue.Occurrences
                })
                .OrderByDescending(issue => issue.Severity == "Critical")
                .ThenByDescending(issue => issue.LastSeen)
                .ToList();
        }
    }
}
