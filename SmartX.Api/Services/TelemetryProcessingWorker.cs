using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed class TelemetryProcessingWorker : BackgroundService
{
    private readonly TelemetryQueue _queue;
    private readonly TelemetryService _telemetryService;
    private readonly ILogger<TelemetryProcessingWorker> _logger;
    private long _processed;
    private long _failed;
    private readonly Queue<TelemetryProcessingEvent> _recentProcessing = [];
    private readonly object _historySync = new();
    private long _processingOrder;

    public TelemetryProcessingWorker(
        TelemetryQueue queue,
        TelemetryService telemetryService,
        ILogger<TelemetryProcessingWorker> logger)
    {
        _queue = queue;
        _telemetryService = telemetryService;
        _logger = logger;
    }

    public TelemetryProcessingStatus GetStatus()
    {
        return new TelemetryProcessingStatus(
            _queue.Count,
            _queue.Capacity,
            Interlocked.Read(ref _processed),
            Interlocked.Read(ref _failed));
    }

    public IReadOnlyList<TelemetryProcessingEvent> GetRecentProcessing()
    {
        lock (_historySync)
        {
            return _recentProcessing.ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var item = await _queue.DequeueAsync(stoppingToken);
                var latency = System.Diagnostics.Stopwatch.GetElapsedTime(item.EnqueuedTimestamp).TotalMilliseconds;
                var succeeded = false;

                try
                {
                    switch (item)
                    {
                        case TelemetryWorkItem.Environmental environmental:
                            _telemetryService.IngestEnvironmental(environmental.Packet);
                            break;
                        case TelemetryWorkItem.Power power:
                            _telemetryService.IngestQueuedPower(power);
                            break;
                        case TelemetryWorkItem.Actuator actuator:
                            _telemetryService.IngestActuator(actuator.Packet);
                            break;
                        default:
                            throw new InvalidOperationException("Unsupported telemetry work item.");
                    }

                    Interlocked.Increment(ref _processed);
                    succeeded = true;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _failed);
                    _logger.LogError(exception, "Failed to process queued telemetry.");
                }
                finally
                {
                    var sensorId = item switch
                    {
                        TelemetryWorkItem.Environmental environmental => environmental.Packet.SensorId,
                        TelemetryWorkItem.Power power => power.Packet.SensorId,
                        TelemetryWorkItem.Actuator actuator => actuator.Packet.SensorId,
                        _ => Guid.Empty
                    };
                    lock (_historySync)
                    {
                        _recentProcessing.Enqueue(new TelemetryProcessingEvent(
                            item.Id, sensorId, item.ArrivalOrder, ++_processingOrder,
                            TelemetryAlertPolicy.IsCritical(item), item.EnqueuedAt,
                            DateTimeOffset.UtcNow, latency, succeeded));
                        if (_recentProcessing.Count > 200)
                        {
                            _recentProcessing.Dequeue();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
