using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class DataStructureTests
{
    [Fact]
    public void GenericPacketPreservesFloatType()
    {
        var packet = new TelemetryPacket<float>(
            Guid.NewGuid(),
            23.5f,
            DateTimeOffset.UtcNow,
            "°C");

        Assert.IsType<float>(packet.Value);
        Assert.Equal(23.5f, packet.Value);
    }

    [Fact]
    public void PowerReadingsCanBeAggregated()
    {
        var first = new PowerReading(1200);
        var second = new PowerReading(800);

        var total = first + second;
        var difference = first - second;

        Assert.Equal(2000, total.Watts);
        Assert.Equal(400, difference.Watts);
        Assert.True(first > second);
    }

    [Fact]
    public void JaggedBatchesConvertToAnOptimizedList()
    {
        var buffer = new TelemetryBatchBuffer<int>(3);

        buffer.AddBatch([1, 2]);
        buffer.AddBatch([3, 4, 5]);

        var result = buffer.ToOptimizedList();

        Assert.Equal([1, 2, 3, 4, 5], result);
        Assert.Equal(2, buffer.BatchCount);
    }

    [Fact]
    public void OldestBatchIsRemovedAtCapacity()
    {
        var buffer = new TelemetryBatchBuffer<int>(2);

        buffer.AddBatch([1]);
        buffer.AddBatch([2]);
        buffer.AddBatch([3]);

        Assert.Equal([2, 3], buffer.ToOptimizedList());
    }

    [Fact]
    public void ItemLimitTrimsOnlyTheOldestItems()
    {
        var buffer = new TelemetryBatchBuffer<int>(10, 5);

        buffer.AddBatch([1, 2, 3]);
        buffer.AddBatch([4, 5, 6]);

        Assert.Equal([2, 3, 4, 5, 6], buffer.ToOptimizedList());

        buffer.AddBatch([7, 8, 9, 10]);

        Assert.Equal([6, 7, 8, 9, 10], buffer.ToOptimizedList());
    }

    [Fact]
    public void OversizedBatchRetainsItsNewestItems()
    {
        var buffer = new TelemetryBatchBuffer<int>(10, 3);
        buffer.AddBatch([1, 2]);
        buffer.AddBatch([3, 4, 5, 6]);

        Assert.Equal([4, 5, 6], buffer.ToOptimizedList());
        Assert.Equal(1, buffer.BatchCount);
    }

    [Fact]
    public void DrainPreservesOrderAndReleasesPendingBatches()
    {
        var buffer = new TelemetryBatchBuffer<int>(3);
        buffer.AddBatch([1, 2]);
        buffer.AddBatch([]);
        buffer.AddBatch([3, 4, 5]);

        var result = buffer.DrainToOptimizedList();

        Assert.Equal([1, 2, 3, 4, 5], result);
        Assert.Equal(result.Count, result.Capacity);
        Assert.Equal(0, buffer.BatchCount);
        Assert.Empty(buffer.ToOptimizedList());
        Assert.Empty(buffer.DrainToOptimizedList());

        buffer.AddBatch([6]);

        Assert.Equal([6], buffer.DrainToOptimizedList());
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    [Theory]
    [InlineData(1, 5, new[] { 4, 5, 6 })]
    [InlineData(2, 5, new[] { 2, 3, 4, 5, 6 })]
    [InlineData(2, 2, new[] { 5, 6 })]
    public void DrainRespectsCapacityAndResetsItemAccounting(
        int maximumBatches, int maximumItems, int[] expected)
    {
        var buffer = new TelemetryBatchBuffer<int>(maximumBatches, maximumItems);
        buffer.AddBatch([1, 2, 3]);
        buffer.AddBatch([4, 5, 6]);

        Assert.Equal(expected, buffer.DrainToOptimizedList());

        buffer.AddBatch([7, 8]);

        Assert.Equal([7, 8], buffer.DrainToOptimizedList());
        Assert.Equal(0, buffer.BatchCount);
    }

    [Fact]
    public void ConcurrentDrainsReturnEveryPendingItemOnce()
    {
        var buffer = new TelemetryBatchBuffer<int>(200);
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 200, index =>
        {
            buffer.AddBatch([index]);
            foreach (var item in buffer.DrainToOptimizedList())
            {
                results.Add(item);
            }
        });

        Assert.Equal(Enumerable.Range(0, 200), results.Order());
        Assert.Empty(buffer.DrainToOptimizedList());
    }
}
