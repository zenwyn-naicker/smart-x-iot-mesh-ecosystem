using System.Diagnostics.CodeAnalysis;
using SmartX.Api.Models;

namespace SmartX.Api.DataStructures;

public sealed class TelemetryQueue
{
    private readonly Queue<TelemetryWorkItem> _items = [];
    private readonly PriorityQueue<TelemetryWorkItem, long> _criticalItems = new();
    private readonly object _sync = new();
    private readonly PowerTelemetryClassifier _powerClassifier;
    private long _arrivalSequence;
    private bool _processingPaused;
    private TaskCompletionSource _itemsAvailable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TelemetryQueue(int capacity, PowerTelemetryClassifier? powerClassifier = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _powerClassifier = powerClassifier ?? new PowerTelemetryClassifier();
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count + _criticalItems.Count;
            }
        }
    }

    public bool TryEnqueue(TelemetryWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_sync)
        {
            if (_items.Count + _criticalItems.Count >= Capacity)
            {
                return false;
            }

            if (item is TelemetryWorkItem.Power power)
            {
                power.HasPowerChange = _powerClassifier.ClassifyAndTrack(power.Packet);
            }

            var critical = TelemetryAlertPolicy.IsCritical(item);
            item.ArrivalOrder = ++_arrivalSequence;
            item.EnqueuedAt = DateTimeOffset.UtcNow;
            item.EnqueuedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            if (critical)
            {
                _criticalItems.Enqueue(item, item.ArrivalOrder);
            }
            else
            {
                _items.Enqueue(item);
            }

            if (!_processingPaused)
            {
                _itemsAvailable.TrySetResult();
            }
            return true;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out TelemetryWorkItem? item)
    {
        lock (_sync)
        {
            return TryDequeueLocked(out item);
        }
    }

    public async ValueTask<TelemetryWorkItem> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task available;

            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryDequeueLocked(out var item))
                {
                    return item;
                }

                available = _itemsAvailable.Task;
            }

            await available.WaitAsync(cancellationToken);
        }
    }

    internal void PauseProcessing()
    {
        lock (_sync)
        {
            _processingPaused = true;
            _itemsAvailable.TrySetResult();
            _itemsAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void ResumeProcessing()
    {
        lock (_sync)
        {
            _processingPaused = false;
            if (_items.Count + _criticalItems.Count > 0)
            {
                _itemsAvailable.TrySetResult();
            }
        }
    }

    private bool TryDequeueLocked([NotNullWhen(true)] out TelemetryWorkItem? item)
    {
        if (_processingPaused)
        {
            item = null;
            return false;
        }

        var dequeued = _criticalItems.TryDequeue(out item, out _) ||
            _items.TryDequeue(out item);

        if (dequeued && _items.Count + _criticalItems.Count == 0)
        {
            _itemsAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return dequeued;
    }
}
