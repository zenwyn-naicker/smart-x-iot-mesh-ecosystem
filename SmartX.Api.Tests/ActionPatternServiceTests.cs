using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class ActionPatternServiceTests
{
    private readonly InMemorySensorRepository _repository = new();
    private readonly ActiveIssueService _issues = new();
    private readonly UserActivityService _activity;
    private readonly ActionPatternService _patterns;
    private readonly Sensor _meter;
    private readonly Guid _relay = Guid.NewGuid();

    public ActionPatternServiceTests()
    {
        _activity = new UserActivityService(_repository, _issues);
        _patterns = new ActionPatternService(_activity);
        _meter = new Sensor
        {
            DeviceIdentifier = "Meter", Facility = "Facility", Zone = "Zone", Node = "Meter",
            Category = SensorCategory.PowerConsumption, LastHeartbeat = DateTimeOffset.UtcNow
        };
        _repository.Add(_meter);
        _issues.Record(_meter, "Telemetry", "Power is above the expected range.", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RepeatedCommandsLearnSeparateOnAndOffPatternsWithEvidenceDates()
    {
        var first = RecordCommand(false);
        Assert.Empty(_patterns.GetPatterns());
        var second = RecordCommand(false);
        RecordCommand(true);
        RecordCommand(true);

        var patterns = _patterns.GetPatterns();
        Assert.Equal(2, patterns.Count);
        var off = Assert.Single(patterns, pattern => pattern.Action == LearnedAction.TurnOff);
        Assert.Equal(_meter.Id, off.Condition.SensorId);
        Assert.Equal(_relay, off.TargetSensorId);
        Assert.Equal(2, off.EvidenceCount);
        Assert.Equal(first.ExecutedAt, off.FirstObservedAt);
        Assert.Equal(second.ExecutedAt, off.LastObservedAt);
        Assert.Equal(patterns, _patterns.GetPatterns());
    }

    [Fact]
    public void OnlySearchesForOneSpecificDeviceLearnInspectionPatterns()
    {
        _activity.RecordSearch(new CommandStreamQuery { Category = SensorCategory.Actuator }, [_relay]);
        _activity.RecordSearch(new CommandStreamQuery { Search = "Relay" }, [_relay, Guid.NewGuid()]);
        _activity.RecordSearch(new CommandStreamQuery { Search = "Missing" }, []);
        Assert.Empty(_patterns.GetPatterns());
        _activity.RecordSearch(new CommandStreamQuery { Search = "Relay" }, [_relay]);
        _activity.RecordSearch(new CommandStreamQuery { Search = " relay " }, [_relay]);

        var pattern = Assert.Single(_patterns.GetPatterns());
        Assert.Equal(LearnedAction.InspectDevice, pattern.Action);
        Assert.Equal(_relay, pattern.TargetSensorId);
        Assert.Equal(2, pattern.EvidenceCount);
    }

    [Fact]
    public void RejectedUnchangedDuplicatedAndUndoneCommandsDoNotInflateEvidence()
    {
        var first = RecordCommand(false);
        _activity.RecordCommand(first);
        RecordCommand(false, DeviceCommandOutcome.Rejected);
        RecordCommand(false, DeviceCommandOutcome.NoChange);
        Assert.Empty(_patterns.GetPatterns());
        var second = RecordCommand(false);
        Assert.Single(_patterns.GetPatterns());
        _activity.RecordCommand(new DeviceCommandResult(Guid.NewGuid(), _relay, false, true,
            DateTimeOffset.UtcNow, DeviceCommandOutcome.Rejected, "Undo rejected", 3, second.Id));
        Assert.Single(_patterns.GetPatterns());
        _activity.RecordCommand(new DeviceCommandResult(Guid.NewGuid(), _relay, false, true,
            DateTimeOffset.UtcNow, DeviceCommandOutcome.Succeeded, "Restored", 3, second.Id));
        Assert.Empty(_patterns.GetPatterns());
    }

    [Fact]
    public void DifferentConditionsDoNotMergeAndClearedAlertsDoNotEraseLearnedPatterns()
    {
        RecordCommand(false);
        RecordCommand(false);
        _issues.Record(_meter, "Telemetry", "Power changed suddenly.", DateTimeOffset.UtcNow);
        RecordCommand(false);
        var original = Assert.Single(_patterns.GetPatterns());
        Assert.Equal("Power is above the expected range.", original.Condition.Message);
        RecordCommand(false);
        Assert.Equal(2, _patterns.GetPatterns().Count);
        _issues.Resolve(_meter.Id, "Telemetry");
        RecordCommand(false);
        Assert.All(_patterns.GetPatterns(), pattern => Assert.Equal(2, pattern.EvidenceCount));
    }

    [Fact]
    public void LearningUsesOnlyRetainedHistory()
    {
        RecordCommand(false);
        RecordCommand(false);
        Assert.Single(_patterns.GetPatterns());
        for (var index = 0; index < 1000; index++)
        {
            _activity.RecordSearch(new CommandStreamQuery { Category = SensorCategory.Actuator }, [_relay]);
        }

        Assert.Empty(_patterns.GetPatterns());
    }

    [Fact]
    public void DisconnectPatternsAreSeparateFromTelemetryPatterns()
    {
        _repository.GetById(_meter.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        RecordCommand(false);
        RecordCommand(false);

        var patterns = _patterns.GetPatterns();
        Assert.Equal(2, patterns.Count);
        Assert.Contains(patterns, pattern => pattern.Condition.IssueType == "Disconnect");
        Assert.Contains(patterns, pattern => pattern.Condition.IssueType == "Telemetry");
    }

    private DeviceCommandResult RecordCommand(bool isOn, DeviceCommandOutcome outcome = DeviceCommandOutcome.Succeeded)
    {
        var command = new DeviceCommandResult(Guid.NewGuid(), _relay, !isOn, isOn,
            DateTimeOffset.UtcNow, outcome, "Command result", 2);
        _activity.RecordCommand(command);
        return command;
    }
}
