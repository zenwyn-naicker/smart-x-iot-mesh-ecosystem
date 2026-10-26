using System.ComponentModel.DataAnnotations;

namespace SmartX.Api.Models;

public sealed class StartSimulationRequest
{
    [Range(1, 20)]
    public int ReadingsPerSecond { get; set; } = 1;
}

public sealed record SimulationSeedResult(int Sensors, int Packets);

public sealed record SimulationStatus(
    bool IsRunning,
    int ReadingsPerSecond,
    string? Error,
    bool RelayDisconnected = false)
{
    public IReadOnlyList<Guid> PausedSensorIds { get; init; } = Array.Empty<Guid>();
}

public enum SimulationScenario
{
    Normal,
    PowerSpike,
    Disconnect,
    Reconnect
}

public sealed class SimulationScenarioRequest
{
    [Required]
    [EnumDataType(typeof(SimulationScenario))]
    public SimulationScenario? Scenario { get; set; }
}

public sealed record SimulationScenarioResult(
    SimulationScenario Scenario,
    Guid MeterId,
    Guid RelayId,
    IReadOnlyList<Guid> PacketIds,
    string Message);
