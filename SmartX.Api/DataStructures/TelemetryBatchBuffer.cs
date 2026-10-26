namespace SmartX.Api.DataStructures;

public sealed class TelemetryBatchBuffer<T>
{
    private readonly T[][] _batches;
    private readonly object _sync = new();
    private readonly int _maximumItems;
    private int _batchCount;
    private int _itemCount;

    public TelemetryBatchBuffer(
        int maximumBatches,
        int maximumItems = int.MaxValue)
    {
        if (maximumBatches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBatches));
        }

        if (maximumItems < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumItems));
        }

        _batches = new T[maximumBatches][];
        _maximumItems = maximumItems;
    }

    public int BatchCount
    {
        get
        {
            lock (_sync)
            {
                return _batchCount;
            }
        }
    }

    public void AddBatch(T[] batch)
    {
        if (batch.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (batch.Length > _maximumItems)
            {
                batch = batch[^_maximumItems..];
            }

            if (_batchCount == _batches.Length)
            {
                RemoveOldestBatch();
            }

            while (_itemCount > _maximumItems - batch.Length)
            {
                var excess = _itemCount - (_maximumItems - batch.Length);

                if (_batches[0].Length <= excess)
                {
                    RemoveOldestBatch();
                }
                else
                {
                    _batches[0] = _batches[0][excess..];
                    _itemCount -= excess;
                }
            }

            _batches[_batchCount] = batch;
            _batchCount++;
            _itemCount += batch.Length;
        }
    }

    public List<T> ToOptimizedList()
    {
        lock (_sync)
        {
            var result = new List<T>(_itemCount);

            for (var index = 0; index < _batchCount; index++)
            {
                result.AddRange(_batches[index]);
            }

            return result;
        }
    }

    public List<T> DrainToOptimizedList()
    {
        lock (_sync)
        {
            var result = ToOptimizedList();
            Array.Clear(_batches, 0, _batchCount);
            _batchCount = 0;
            _itemCount = 0;
            return result;
        }
    }

    private void RemoveOldestBatch()
    {
        _itemCount -= _batches[0].Length;
        Array.Copy(_batches, 1, _batches, 0, _batchCount - 1);
        _batchCount--;
        _batches[_batchCount] = [];
    }
}
