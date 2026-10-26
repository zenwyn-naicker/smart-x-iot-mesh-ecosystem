using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class TelemetryHistoryTreeRangeTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTreeReturnsAnEmptyRange()
    {
        var result = new TelemetryHistoryTree().GetRange(Timestamp, Timestamp);

        Assert.Empty(result.Readings);
        Assert.False(result.HasMore);
    }

    [Fact]
    public void RangeIncludesBothBoundariesAndReturnsChronologicalReadings()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 5).Select(index => Reading(Timestamp.AddSeconds(index))).ToArray();
        foreach (var index in new[] { 3, 0, 4, 1, 2 })
        {
            tree.Add(index, readings[index]);
        }

        var result = tree.GetRange(readings[1].Timestamp, readings[3].Timestamp);

        Assert.Equal(readings.Skip(1).Take(3), result.Readings);
        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData(-10, -1)]
    [InlineData(11, 20)]
    [InlineData(1, 9)]
    public void NonMatchingRangesReturnNoReadings(int fromSeconds, int toSeconds)
    {
        var tree = new TelemetryHistoryTree();
        tree.Add(0, Reading(Timestamp));
        tree.Add(1, Reading(Timestamp.AddSeconds(10)));

        var result = tree.GetRange(Timestamp.AddSeconds(fromSeconds), Timestamp.AddSeconds(toSeconds), 1);

        Assert.Empty(result.Readings);
        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    [InlineData(int.MaxValue, false)]
    public void LimitReturnsTheNewestMatchesAndReportsOnlyAdditionalMatches(int limit, bool hasMore)
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 5).Select(index => Reading(Timestamp.AddSeconds(index))).ToArray();
        for (var index = 0; index < readings.Length; index++)
        {
            tree.Add(index, readings[index]);
        }

        var result = tree.GetRange(readings[1].Timestamp, readings[3].Timestamp, limit);

        Assert.Equal(readings.Skip(1).Take(3).TakeLast(limit), result.Readings);
        Assert.Equal(hasMore, result.HasMore);
    }

    [Fact]
    public void EqualTimestampsAreIncludedAndLimitedBySequence()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 5).Select(_ => Reading(Timestamp)).ToArray();
        foreach (var index in new[] { 3, 1, 4, 0, 2 })
        {
            tree.Add(index, readings[index]);
        }
        tree.Add(5, Reading(Timestamp.AddTicks(-1)));
        tree.Add(6, Reading(Timestamp.AddTicks(1)));

        var limited = tree.GetRange(Timestamp, Timestamp, 2);
        var complete = tree.GetRange(Timestamp, Timestamp, 5);

        Assert.Equal(readings.TakeLast(2), limited.Readings);
        Assert.True(limited.HasMore);
        Assert.Equal(readings, complete.Readings);
        Assert.False(complete.HasMore);
    }

    [Fact]
    public void RangeComparesUtcInstantsAcrossDifferentOffsets()
    {
        var tree = new TelemetryHistoryTree();
        var first = Reading(Timestamp.ToOffset(TimeSpan.FromHours(2)));
        var second = Reading(Timestamp.AddMinutes(1).ToOffset(TimeSpan.FromHours(-3)));
        tree.Add(0, first);
        tree.Add(1, second);
        tree.Add(2, Reading(Timestamp.AddMinutes(2)));

        var result = tree.GetRange(
            Timestamp.ToOffset(TimeSpan.FromHours(-5)),
            Timestamp.AddMinutes(1).ToOffset(TimeSpan.FromHours(7)));

        Assert.Equal(new[] { first, second }, result.Readings);
        Assert.False(result.HasMore);
    }

    [Fact]
    public void DefaultLimitReturnsTwoHundredNewestReadings()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 201).Select(index => Reading(Timestamp.AddTicks(index))).ToArray();
        for (var index = 0; index < readings.Length; index++)
        {
            tree.Add(index, readings[index]);
        }

        var result = tree.GetRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.Equal(readings.Skip(1), result.Readings);
        Assert.True(result.HasMore);
    }

    [Fact]
    public void MixedTimestampQueriesMatchAnIndependentSortedReference()
    {
        var tree = new TelemetryHistoryTree();
        var random = new Random(7312);
        var readings = Enumerable.Range(0, 3000)
            .Select(sequence => (Sequence: sequence, Reading: Reading(Timestamp.AddSeconds(random.Next(1000)))))
            .ToArray();
        foreach (var entry in readings)
        {
            tree.Add(entry.Sequence, entry.Reading);
        }
        var height = tree.Height;

        for (var query = 0; query < 100; query++)
        {
            var from = Timestamp.AddSeconds(random.Next(-100, 1100));
            var to = from.AddSeconds(random.Next(100));
            var limit = random.Next(1, 25);
            var expected = readings
                .Where(entry => entry.Reading.Timestamp >= from && entry.Reading.Timestamp <= to)
                .OrderBy(entry => entry.Reading.Timestamp)
                .ThenBy(entry => entry.Sequence)
                .Select(entry => entry.Reading)
                .ToArray();

            var result = tree.GetRange(from, to, limit);

            Assert.Equal(expected.TakeLast(limit), result.Readings);
            Assert.Equal(expected.Length > limit, result.HasMore);
        }

        Assert.Equal(readings.Length, tree.Count);
        Assert.Equal(height, tree.Height);
    }

    [Fact]
    public void ResultRemainsUnchangedAfterFurtherQueriesAndInsertions()
    {
        var tree = new TelemetryHistoryTree();
        var first = Reading(Timestamp);
        tree.Add(0, first);
        var result = tree.GetRange(Timestamp, Timestamp);
        tree.Add(1, Reading(Timestamp));

        Assert.True(tree.GetRange(Timestamp, Timestamp, 1).HasMore);
        Assert.Same(first, Assert.Single(result.Readings));
        Assert.False(result.HasMore);
    }

    [Fact]
    public void ReversedRangeIsRejected()
    {
        var tree = new TelemetryHistoryTree();

        Assert.Throws<ArgumentException>(() => tree.GetRange(Timestamp.AddTicks(1), Timestamp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimitsAreRejected(int limit)
    {
        var tree = new TelemetryHistoryTree();

        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetRange(Timestamp, Timestamp, limit));
    }

    private static TelemetryReadingResponse Reading(DateTimeOffset timestamp)
    {
        return new TelemetryReadingResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Environmental", "23.5", "°C",
            timestamp, false, null, 10, 35);
    }
}
