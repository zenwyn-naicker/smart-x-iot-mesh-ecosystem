namespace SmartX.Api.Models;

public abstract record TelemetryWorkItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public long ArrivalOrder { get; internal set; }
    public DateTimeOffset EnqueuedAt { get; internal set; }
    internal long EnqueuedTimestamp { get; set; }

    public sealed record Environmental(TelemetryPacket<float> Packet)
        : TelemetryWorkItem;

    public sealed record Power(TelemetryPacket<int> Packet)
        : TelemetryWorkItem
    {
        internal bool? HasPowerChange { get; set; }
    }

    public sealed record Actuator(TelemetryPacket<bool> Packet)
        : TelemetryWorkItem;
}
