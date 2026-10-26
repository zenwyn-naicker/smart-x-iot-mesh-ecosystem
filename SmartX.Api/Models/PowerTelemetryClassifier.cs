namespace SmartX.Api.Models;

public sealed class PowerTelemetryClassifier
{
    private readonly Dictionary<Guid, TelemetryPacket<int>> _latest = [];
    private readonly object _sync = new();

    public bool ClassifyAndTrack(TelemetryPacket<int> packet)
    {
        lock (_sync)
        {
            var previous = _latest.GetValueOrDefault(packet.SensorId);
            if (previous is not null && packet.Timestamp <= previous.Timestamp)
            {
                return false;
            }

            _latest[packet.SensorId] = packet;
            return previous is not null &&
                Math.Abs((long)packet.Value - previous.Value) > 1500;
        }
    }
}
