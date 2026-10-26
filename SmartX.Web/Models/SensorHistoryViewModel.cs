using System.ComponentModel.DataAnnotations;

namespace SmartX.Web.Models;

public sealed class SensorHistoryFilter
{
    [Required]
    public DateTime? From { get; set; } = DateTime.UtcNow.AddHours(-1);

    [Required]
    public DateTime? To { get; set; } = DateTime.UtcNow;

    [Range(1, 1000)]
    public int Limit { get; set; } = 200;
}

public sealed record SensorHistoryRangeViewModel(
    IReadOnlyList<TelemetryReadingViewModel> Readings, bool HasMore);

public sealed class SensorHistoryViewModel
{
    public SensorViewModel Sensor { get; set; } = new();
    public SensorHistoryFilter Filters { get; init; } = new();
    public SensorHistoryRangeViewModel? History { get; set; }
}
