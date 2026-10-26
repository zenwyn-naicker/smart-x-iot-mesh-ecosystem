using System.ComponentModel.DataAnnotations;

namespace SmartX.Web.Models;

public enum CommandStreamAlertFilter
{
    All,
    Active,
    Telemetry,
    Disconnected
}

public sealed class CommandStreamFilter
{
    [StringLength(200)]
    public string? Search { get; set; }

    [EnumDataType(typeof(SensorCategory))]
    public SensorCategory? Category { get; set; }

    [EnumDataType(typeof(CommandStreamAlertFilter))]
    public CommandStreamAlertFilter Alert { get; set; }

    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) ||
        Category is not null || Alert != CommandStreamAlertFilter.All;
}
