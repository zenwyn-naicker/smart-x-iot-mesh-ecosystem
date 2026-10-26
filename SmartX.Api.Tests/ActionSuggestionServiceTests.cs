using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class ActionSuggestionServiceTests
{
    private readonly InMemorySensorRepository _repository = new();
    private readonly ActiveIssueService _issues = new();
    private readonly UserActivityService _activity;
    private readonly DeviceCommandService _commands;
    private readonly TelemetryService _telemetry;
    private readonly ActionSuggestionService _suggestions;
    private readonly Sensor _source;
    private readonly Sensor _relay;

    public ActionSuggestionServiceTests()
    {
        _activity = new UserActivityService(_repository, _issues);
        _commands = new DeviceCommandService(_repository, _activity);
        _telemetry = new TelemetryService(_repository, _issues, _commands);
        _suggestions = new ActionSuggestionService(new ActionPatternService(_activity),
            new DashboardService(_repository, _telemetry, _issues), _commands);
        _source = AddSensor("Meter", SensorCategory.PowerConsumption);
        _relay = AddSensor("Relay", SensorCategory.Actuator);
        _telemetry.IngestActuator(new(_relay.Id, false, DateTimeOffset.UtcNow, "state"));
        _issues.Record(_source, "Telemetry", "Power spike.", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void SuggestionsExplainEvidenceWithoutChangingStateOrRecordingActivity()
    {
        Assert.Empty(_suggestions.GetSuggestions());
        Train(_relay.Id, 2);
        var before = _activity.GetHistory().Count;
        var suggestion = Assert.Single(_suggestions.GetSuggestions());
        Assert.Equal(_relay.Id, suggestion.TargetSensorId);
        Assert.Equal(LearnedAction.TurnOn, suggestion.Action);
        Assert.Equal(2, suggestion.EvidenceCount);
        Assert.Equal(_source.Id, suggestion.Condition.SensorId);
        Assert.Contains("Meter: Power spike.", suggestion.Reason);
        Assert.Contains("2 times", suggestion.Reason);
        Assert.Equal(suggestion, Assert.Single(_suggestions.GetSuggestions()));
        Assert.Equal(before, _activity.GetHistory().Count);
        Assert.False(_commands.GetState(_relay.Id)!.IsOn);
        Assert.Empty(_commands.GetHistory());
    }

    [Fact]
    public void ClearedChangedAndStaleTelemetryConditionsDoNotTriggerSuggestions()
    {
        Train(_relay.Id, 2);
        _issues.Resolve(_source.Id, "Telemetry");
        Assert.Empty(_suggestions.GetSuggestions());
        _issues.Record(_source, "Telemetry", "Different condition.", DateTimeOffset.UtcNow);
        Assert.Empty(_suggestions.GetSuggestions());
        _issues.Record(_source, "Telemetry", "Power spike.", DateTimeOffset.UtcNow);
        Assert.Single(_suggestions.GetSuggestions());
        _repository.GetById(_source.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        Assert.Empty(_suggestions.GetSuggestions());
    }

    [Fact]
    public void DisconnectedUnknownNonActuatorAndMissingTargetsAreExcludedFromCommands()
    {
        var unknown = AddSensor("Unknown relay", SensorCategory.Actuator);
        Train(unknown.Id, 2);
        Train(_source.Id, 2);
        Train(Guid.NewGuid(), 2);
        Train(_relay.Id, 2);
        _repository.GetById(_relay.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        Assert.Empty(_suggestions.GetSuggestions());
        _repository.UpdateHeartbeat(_relay.Id, DateTimeOffset.UtcNow);
        Assert.Equal(_relay.Id, Assert.Single(_suggestions.GetSuggestions()).TargetSensorId);
    }

    [Fact]
    public void ApplyingSuggestedStateRemovesTheSuggestion()
    {
        Train(_relay.Id, 2);
        Train(_relay.Id, 2, false);
        Assert.Equal(LearnedAction.TurnOn, Assert.Single(_suggestions.GetSuggestions()).Action);
        _commands.Execute(_relay.Id, true);
        Assert.Equal(LearnedAction.TurnOff, Assert.Single(_suggestions.GetSuggestions()).Action);
    }

    [Fact]
    public void RankingUsesEvidenceThenRecencyAndAppliesTheLimitLast()
    {
        var second = AddSensor("Second relay", SensorCategory.Actuator);
        var third = AddSensor("Third relay", SensorCategory.Actuator);
        _telemetry.IngestActuator(new(second.Id, false, DateTimeOffset.UtcNow, "state"));
        _telemetry.IngestActuator(new(third.Id, false, DateTimeOffset.UtcNow, "state"));
        var now = DateTimeOffset.UtcNow;
        Train(_relay.Id, 3, observedAt: now.AddMinutes(-2));
        Train(second.Id, 2, observedAt: now.AddMinutes(-1));
        Train(third.Id, 2, observedAt: now);
        var suggestions = _suggestions.GetSuggestions(2);
        Assert.Equal(new[] { _relay.Id, third.Id }, suggestions.Select(suggestion => suggestion.TargetSensorId));
    }

    [Fact]
    public void MultipleConditionsProduceOneSuggestionPerDeviceAndAction()
    {
        var secondSource = AddSensor("Second meter", SensorCategory.PowerConsumption);
        _issues.Record(secondSource, "Telemetry", "Another spike.", DateTimeOffset.UtcNow);
        Train(_relay.Id, 2);
        var suggestion = Assert.Single(_suggestions.GetSuggestions());
        Assert.Equal(2, suggestion.EvidenceCount);
    }

    [Fact]
    public void LearnedDisconnectSearchesSuggestInspectionUntilReconnection()
    {
        _issues.Resolve(_source.Id, "Telemetry");
        _repository.GetById(_source.Id)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        for (var index = 0; index < 2; index++)
        {
            _activity.RecordSearch(new CommandStreamQuery { Search = "Meter" }, [_source.Id]);
        }

        var suggestion = Assert.Single(_suggestions.GetSuggestions());
        Assert.Equal(LearnedAction.InspectDevice, suggestion.Action);
        Assert.Equal("Disconnect", suggestion.Condition.IssueType);
        _repository.UpdateHeartbeat(_source.Id, DateTimeOffset.UtcNow);
        Assert.Empty(_suggestions.GetSuggestions());
    }

    private Sensor AddSensor(string identifier, SensorCategory category)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = identifier, Category = category,
            Facility = "Facility", Zone = "Zone", Node = identifier,
            LastHeartbeat = DateTimeOffset.UtcNow
        };
        _repository.Add(sensor);
        return sensor;
    }

    private void Train(Guid target, int count, bool isOn = true, DateTimeOffset? observedAt = null)
    {
        for (var index = 0; index < count; index++)
        {
            _activity.RecordCommand(new DeviceCommandResult(Guid.NewGuid(), target, !isOn, isOn,
                observedAt ?? DateTimeOffset.UtcNow, DeviceCommandOutcome.Succeeded, "Updated", 2));
        }
    }
}
