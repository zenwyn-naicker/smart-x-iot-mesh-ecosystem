using System.Collections.Concurrent;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class TelemetryQueueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapacityMustBePositive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryQueue(capacity));
    }

    [Fact]
    public void MixedPacketsAreDequeuedInArrivalOrderWithTheirOriginalTypes()
    {
        var queue = new TelemetryQueue(3);
        var timestamp = DateTimeOffset.UtcNow;
        var environmental = new TelemetryWorkItem.Environmental(
            new(Guid.NewGuid(), 23.5f, timestamp, "C"));
        var power = new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), 1200, timestamp.AddSeconds(-1), "W"));
        var actuator = new TelemetryWorkItem.Actuator(
            new(Guid.NewGuid(), true, timestamp.AddSeconds(1), "state"));

        Assert.True(queue.TryEnqueue(environmental));
        Assert.True(queue.TryEnqueue(power));
        Assert.True(queue.TryEnqueue(actuator));
        Assert.Equal(3, queue.Count);

        Assert.True(queue.TryDequeue(out var first));
        Assert.Same(environmental, first);
        Assert.Equal(23.5f, Assert.IsType<TelemetryWorkItem.Environmental>(first).Packet.Value);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Same(power, second);
        Assert.Equal(1200, Assert.IsType<TelemetryWorkItem.Power>(second).Packet.Value);
        Assert.True(queue.TryDequeue(out var third));
        Assert.Same(actuator, third);
        Assert.True(Assert.IsType<TelemetryWorkItem.Actuator>(third).Packet.Value);
        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out var empty));
        Assert.Null(empty);
    }

    [Fact]
    public void FullQueueRejectsNewItemsWithoutReplacingAcceptedItems()
    {
        var queue = new TelemetryQueue(1);
        var first = CreateWorkItem();
        var second = CreateWorkItem();

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(second));
        Assert.Equal(queue.Capacity, queue.Count);
        Assert.True(queue.TryDequeue(out var item));
        Assert.Same(first, item);

        Assert.True(queue.TryEnqueue(second));
        Assert.True(queue.TryDequeue(out item));
        Assert.Same(second, item);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void NullItemsAreRejectedWithoutChangingQueue()
    {
        var queue = new TelemetryQueue(1);

        Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ConcurrentAccessRespectsCapacityAndDeliversAcceptedItemsOnce()
    {
        var queue = new TelemetryQueue(100);
        var accepted = new ConcurrentBag<TelemetryWorkItem>();
        var received = new ConcurrentBag<TelemetryWorkItem>();

        Parallel.For(0, 1000, _ =>
        {
            var item = CreateWorkItem();

            if (queue.TryEnqueue(item))
            {
                accepted.Add(item);
            }
        });

        Assert.Equal(queue.Capacity, accepted.Count);
        Assert.Equal(queue.Capacity, queue.Count);

        Parallel.For(0, 1000, _ =>
        {
            if (queue.TryDequeue(out var item))
            {
                received.Add(item);
            }
        });

        Assert.Equal(accepted.Count, received.Count);
        Assert.True(accepted.ToHashSet().SetEquals(received));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CriticalPacketsBypassNormalBacklogInArrivalOrder()
    {
        var queue = new TelemetryQueue(4);
        var firstNormal = CreateWorkItem();
        var secondNormal = CreateWorkItem();
        var timestamp = DateTimeOffset.UtcNow;
        var firstCritical = new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), 4400, timestamp, "W"));
        var secondCritical = new TelemetryWorkItem.Environmental(
            new(Guid.NewGuid(), 44f, timestamp.AddMinutes(-1), "C"));

        Assert.True(queue.TryEnqueue(firstNormal));
        Assert.True(queue.TryEnqueue(firstCritical));
        Assert.True(queue.TryEnqueue(secondNormal));
        Assert.True(queue.TryEnqueue(secondCritical));
        Assert.Equal(4, queue.Count);

        foreach (var expected in new TelemetryWorkItem[]
                 { firstCritical, secondCritical, firstNormal, secondNormal })
        {
            Assert.True(queue.TryDequeue(out var actual));
            Assert.Same(expected, actual);
        }

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out var empty));
        Assert.Null(empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CriticalAndNormalPacketsShareTheCapacityLimit(bool criticalFirst)
    {
        var queue = new TelemetryQueue(1);
        var normal = CreateWorkItem();
        TelemetryWorkItem critical = new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), 4400, DateTimeOffset.UtcNow, "W"));
        var accepted = criticalFirst ? critical : normal;
        var rejected = criticalFirst ? normal : critical;

        Assert.True(queue.TryEnqueue(accepted));
        Assert.False(queue.TryEnqueue(rejected));
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out var actual));
        Assert.Same(accepted, actual);
        Assert.True(queue.TryEnqueue(rejected));
        Assert.True(queue.TryDequeue(out actual));
        Assert.Same(rejected, actual);

        var sensor = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var baseline = new TelemetryWorkItem.Power(new(sensor, 1000, start, "W"));
        var spike = new TelemetryWorkItem.Power(new(sensor, 3000, start.AddSeconds(1), "W"));
        Assert.True(queue.TryEnqueue(baseline));
        Assert.False(queue.TryEnqueue(spike));
        Assert.True(queue.TryDequeue(out _));
        Assert.True(queue.TryEnqueue(spike));
        Assert.True(TelemetryAlertPolicy.IsCritical(spike));
        Assert.True(queue.TryDequeue(out _));

        foreach (var time in new[] { start, start.AddSeconds(1) })
        {
            var late = new TelemetryWorkItem.Power(new(sensor, 0, time, "W"));
            Assert.True(queue.TryEnqueue(late));
            Assert.False(TelemetryAlertPolicy.IsCritical(late));
            Assert.True(queue.TryDequeue(out _));
        }

        var steady = new TelemetryWorkItem.Power(new(sensor, 3100, start.AddSeconds(2), "W"));
        Assert.True(queue.TryEnqueue(steady));
        Assert.False(TelemetryAlertPolicy.IsCritical(steady));
        Assert.True(queue.TryDequeue(out _));
        var otherSensor = new TelemetryWorkItem.Power(new(Guid.NewGuid(), 0, start.AddSeconds(3), "W"));
        Assert.True(queue.TryEnqueue(otherSensor));
        Assert.False(TelemetryAlertPolicy.IsCritical(otherSensor));
    }

    [Fact]
    public async Task AsyncDequeueWakesForEachNewBatchOfItems()
    {
        var queue = new TelemetryQueue(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (var index = 0; index < 2; index++)
        {
            var pending = queue.DequeueAsync(timeout.Token).AsTask();
            Assert.False(pending.IsCompleted);
            var item = CreateWorkItem();

            Assert.True(queue.TryEnqueue(item));
            Assert.Same(item, await pending);
        }
    }

    [Fact]
    public async Task CancelledWaitDoesNotConsumeLaterItems()
    {
        var queue = new TelemetryQueue(1);
        using var cancellation = new CancellationTokenSource();
        var pending = queue.DequeueAsync(cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var item = CreateWorkItem();
        Assert.True(queue.TryEnqueue(item));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.DequeueAsync(cancellation.Token).AsTask());
        Assert.True(queue.TryDequeue(out var actual));
        Assert.Same(item, actual);
    }

    private static TelemetryWorkItem CreateWorkItem()
    {
        return new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), 1200, DateTimeOffset.UtcNow, "W"));
    }
}
