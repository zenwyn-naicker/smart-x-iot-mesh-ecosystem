namespace SmartX.Api.Models;

public static class TelemetryAlertPolicy
{
    public const float MinimumEnvironmentalValue = 10f;
    public const float MaximumEnvironmentalValue = 35f;
    public const int MinimumPowerWatts = 0;
    public const int MaximumPowerWatts = 3500;

    public static bool IsEnvironmentalCritical(float value)
    {
        return value < MinimumEnvironmentalValue || value > MaximumEnvironmentalValue;
    }

    public static bool IsPowerCritical(int watts)
    {
        return watts < MinimumPowerWatts || watts > MaximumPowerWatts;
    }

    public static bool IsCritical(TelemetryWorkItem item)
    {
        return item switch
        {
            TelemetryWorkItem.Environmental environmental =>
                IsEnvironmentalCritical(environmental.Packet.Value),
            TelemetryWorkItem.Power power =>
                IsPowerCritical(power.Packet.Value) || power.HasPowerChange == true,
            TelemetryWorkItem.Actuator => false,
            _ => throw new ArgumentException("Unsupported telemetry work item.", nameof(item))
        };
    }
}
