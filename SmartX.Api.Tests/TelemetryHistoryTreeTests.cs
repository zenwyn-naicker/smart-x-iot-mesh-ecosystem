using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class TelemetryHistoryTreeTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTreeHasNoReadings()
    {
        var tree = new TelemetryHistoryTree();

        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
        Assert.Empty(tree.GetReadings());
    }

    [Theory]
    [InlineData(30, 20, 10)]
    [InlineData(10, 20, 30)]
    [InlineData(30, 10, 20)]
    [InlineData(10, 30, 20)]
    public void RotationsPreserveReadingsAndBalance(int first, int second, int third)
    {
        var tree = new TelemetryHistoryTree();
        var readings = new[] { first, second, third }
            .Select(offset => Reading(Timestamp.AddSeconds(offset)))
            .ToArray();

        for (var index = 0; index < readings.Length; index++)
        {
            tree.Add(index, readings[index]);
        }

        Assert.Equal(3, tree.Count);
        Assert.Equal(2, tree.Height);
        Assert.Equal(readings.OrderBy(reading => reading.Timestamp), tree.GetReadings());
    }

    [Theory]
    [InlineData("ascending")]
    [InlineData("descending")]
    [InlineData("shuffled")]
    public void LargeInsertionsKeepLogarithmicHeightAndEveryReading(string order)
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 3000)
            .Select(index => Reading(Timestamp.AddTicks(index)))
            .ToArray();
        var indexes = Enumerable.Range(0, readings.Length).ToArray();

        if (order == "descending")
        {
            Array.Reverse(indexes);
        }
        else if (order == "shuffled")
        {
            new Random(7312).Shuffle(indexes);
        }

        foreach (var index in indexes)
        {
            tree.Add(index, readings[index]);
            var maximumHeight = (int)Math.Floor(1.4405 * Math.Log2(tree.Count + 2) - 0.328);
            Assert.InRange(tree.Height, 1, maximumHeight);
        }

        Assert.Equal(readings.Length, tree.Count);
        Assert.Equal(readings, tree.GetReadings());
    }

    [Fact]
    public void EqualTimestampsRetainEveryReadingInSequenceOrder()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 4).Select(_ => Reading(Timestamp)).ToArray();

        foreach (var sequence in new[] { 3, 1, 2, 0 })
        {
            tree.Add(sequence, readings[sequence]);
        }

        Assert.Equal(4, tree.Count);
        Assert.Equal(readings, tree.GetReadings());
    }

    [Fact]
    public void TimestampTakesPrecedenceOverSequence()
    {
        var tree = new TelemetryHistoryTree();
        var earlier = Reading(Timestamp.AddMinutes(-1));
        var later = Reading(Timestamp.AddMinutes(1));

        tree.Add(0, later);
        tree.Add(long.MaxValue, earlier);

        Assert.Equal(new[] { earlier, later }, tree.GetReadings());
    }

    [Fact]
    public void EquivalentUtcInstantsUseSequenceAsTheTieBreaker()
    {
        var tree = new TelemetryHistoryTree();
        var first = Reading(Timestamp.ToOffset(TimeSpan.FromHours(2)));
        var second = Reading(Timestamp);

        tree.Add(2, second);
        tree.Add(1, first);

        Assert.Equal(new[] { first, second }, tree.GetReadings());
        Assert.Throws<ArgumentException>(() => tree.Add(1, Reading(Timestamp)));
        Assert.Equal(2, tree.Count);
    }

    [Fact]
    public void DuplicateKeyDoesNotReplaceOrRemoveExistingReadings()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 7)
            .Select(index => Reading(Timestamp.AddSeconds(index)))
            .ToArray();
        for (var index = 0; index < readings.Length; index++)
        {
            tree.Add(index, readings[index]);
        }
        var height = tree.Height;

        Assert.Throws<ArgumentException>(() => tree.Add(0, Reading(readings[0].Timestamp)));

        Assert.Equal(readings.Length, tree.Count);
        Assert.Equal(height, tree.Height);
        Assert.Equal(readings, tree.GetReadings());
        var next = Reading(Timestamp.AddMinutes(1));
        tree.Add(7, next);
        Assert.Equal(readings.Append(next), tree.GetReadings());
    }

    [Fact]
    public void ReturnedReadingsRemainUnchangedAfterAnotherInsertion()
    {
        var tree = new TelemetryHistoryTree();
        var first = Reading(Timestamp);
        tree.Add(0, first);
        var snapshot = tree.GetReadings();
        var second = Reading(Timestamp.AddSeconds(1));

        tree.Add(1, second);

        Assert.Same(first, Assert.Single(snapshot));
        Assert.Equal(new[] { first, second }, tree.GetReadings());
    }

    [Fact]
    public void NullReadingIsRejectedWithoutChangingTheTree()
    {
        var tree = new TelemetryHistoryTree();

        Assert.Throws<ArgumentNullException>(() => tree.Add(0, null!));

        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
        Assert.Empty(tree.GetReadings());
    }

    private static TelemetryReadingResponse Reading(DateTimeOffset timestamp)
    {
        return new TelemetryReadingResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Environmental", "23.5", "°C",
            timestamp, false, null, 10, 35);
    }
}
