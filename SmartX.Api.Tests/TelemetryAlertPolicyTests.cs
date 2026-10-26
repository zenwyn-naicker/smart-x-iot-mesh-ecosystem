using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class TelemetryAlertPolicyTests
{
    [Theory]
    [InlineData(9.9f, true)]
    [InlineData(10f, false)]
    [InlineData(35f, false)]
    [InlineData(35.1f, true)]
    public void EnvironmentalClassificationUsesExistingThresholds(float value, bool critical)
    {
        var item = new TelemetryWorkItem.Environmental(
            new(Guid.NewGuid(), value, DateTimeOffset.UtcNow, "C"));

        Assert.Equal(critical, TelemetryAlertPolicy.IsCritical(item));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(3500, false)]
    [InlineData(3501, true)]
    public void PowerClassificationUsesExistingThresholds(int value, bool critical)
    {
        var item = new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), value, DateTimeOffset.UtcNow, "W"));

        Assert.Equal(critical, TelemetryAlertPolicy.IsCritical(item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActuatorStatesUseNormalQueue(bool value)
    {
        var item = new TelemetryWorkItem.Actuator(
            new(Guid.NewGuid(), value, DateTimeOffset.UtcNow, "state"));

        Assert.False(TelemetryAlertPolicy.IsCritical(item));
    }
}
