namespace SmartX.Web.Models;

public sealed record SimulationStatusViewModel(
    bool IsRunning,
    int ReadingsPerSecond,
    string? Error)
{
    public IReadOnlyList<Guid> PausedSensorIds { get; init; } = Array.Empty<Guid>();
}
