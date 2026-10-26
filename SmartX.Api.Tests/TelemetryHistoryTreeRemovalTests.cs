using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class TelemetryHistoryTreeRemovalTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(new[] { 1 }, 1)]
    [InlineData(new[] { 2, 1, 3 }, 1)]
    [InlineData(new[] { 2, 1 }, 2)]
    [InlineData(new[] { 1, 2 }, 1)]
    [InlineData(new[] { 2, 1, 3 }, 2)]
    [InlineData(new[] { 4, 2, 6, 1, 3, 5, 7 }, 2)]
    [InlineData(new[] { 4, 2, 8, 1, 3, 6, 9, 7 }, 4)]
    public void RemovalPreservesEveryOtherReading(int[] order, int removed)
    {
        var tree = new TelemetryHistoryTree();
        var readings = order.ToDictionary(value => value, value => Reading(Timestamp.AddSeconds(value)));
        foreach (var value in order)
        {
            tree.Add(value, readings[value]);
        }

        Assert.True(tree.Remove(readings[removed].Timestamp, removed));
        Assert.False(tree.Remove(readings[removed].Timestamp, removed));
        Assert.Equal(order.Length - 1, tree.Count);
        Assert.Equal(readings.Where(entry => entry.Key != removed).OrderBy(entry => entry.Key)
            .Select(entry => entry.Value), tree.GetReadings());
        AssertHeight(tree);
    }

    [Theory]
    [InlineData(new[] { 4, 2, 5, 1 }, 5)]
    [InlineData(new[] { 2, 1, 4, 5 }, 1)]
    [InlineData(new[] { 4, 2, 5, 3 }, 5)]
    [InlineData(new[] { 2, 1, 4, 3 }, 1)]
    public void RemovalRebalancesWithSingleAndDoubleRotations(int[] order, int removed)
    {
        var tree = new TelemetryHistoryTree();
        foreach (var value in order)
        {
            tree.Add(value, Reading(Timestamp.AddSeconds(value)));
        }

        Assert.True(tree.Remove(Timestamp.AddSeconds(removed), removed));

        Assert.Equal(2, tree.Height);
        Assert.Equal(order.Where(value => value != removed).Order().Select(value => Timestamp.AddSeconds(value)),
            tree.GetReadings().Select(reading => reading.Timestamp));
    }

    [Fact]
    public void MissingKeysLeaveTheTreeUnchanged()
    {
        var tree = new TelemetryHistoryTree();
        Assert.False(tree.Remove(Timestamp, 0));
        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
        var reading = Reading(Timestamp);
        tree.Add(0, reading);

        Assert.False(tree.Remove(Timestamp, 1));
        Assert.False(tree.Remove(Timestamp.AddTicks(1), 0));
        Assert.Equal(1, tree.Count);
        Assert.Equal(1, tree.Height);
        Assert.Same(reading, Assert.Single(tree.GetReadings()));
    }

    [Fact]
    public void RemovalUsesTheExactSequenceAndUtcInstant()
    {
        var tree = new TelemetryHistoryTree();
        var readings = Enumerable.Range(0, 5).Select(_ => Reading(Timestamp)).ToArray();
        foreach (var sequence in new[] { 2, 0, 4, 1, 3 })
        {
            tree.Add(sequence, readings[sequence]);
        }

        Assert.True(tree.Remove(Timestamp.ToOffset(TimeSpan.FromHours(2)), 2));

        var range = tree.GetRange(Timestamp, Timestamp, 3);
        Assert.Equal(new[] { readings[1], readings[3], readings[4] }, range.Readings);
        Assert.True(range.HasMore);
        Assert.Equal(4, tree.Count);
        Assert.True(tree.Remove(Timestamp, 0));
        Assert.False(tree.GetRange(Timestamp, Timestamp, 3).HasMore);
    }

    [Theory]
    [InlineData("ascending")]
    [InlineData("descending")]
    [InlineData("shuffled")]
    public void RepeatedRemovalKeepsTheTreeBalancedAndAllowsReuse(string order)
    {
        var tree = new TelemetryHistoryTree();
        var indexes = Enumerable.Range(0, 1000).ToArray();
        foreach (var index in indexes)
        {
            tree.Add(index, Reading(Timestamp.AddTicks(index)));
        }
        if (order == "descending")
        {
            Array.Reverse(indexes);
        }
        else if (order == "shuffled")
        {
            new Random(7312).Shuffle(indexes);
        }
        var remaining = new SortedSet<int>(indexes);

        foreach (var index in indexes)
        {
            Assert.True(tree.Remove(Timestamp.AddTicks(index), index));
            remaining.Remove(index);
            Assert.Equal(remaining.Count, tree.Count);
            Assert.Equal(remaining.Select(value => Timestamp.AddTicks(value)),
                tree.GetReadings().Select(reading => reading.Timestamp));
            AssertHeight(tree);
        }

        Assert.Empty(tree.GetRange(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Readings);
        var reading = Reading(Timestamp);
        tree.Add(0, reading);
        Assert.Same(reading, Assert.Single(tree.GetReadings()));
        Assert.Equal(1, tree.Height);
    }

    [Fact]
    public void MixedInsertionsAndRemovalsKeepRangeQueriesConsistent()
    {
        var tree = new TelemetryHistoryTree();
        var expected = new SortedDictionary<(DateTimeOffset Timestamp, long Sequence), TelemetryReadingResponse>();
        var random = new Random(7312);

        for (var sequence = 0; sequence < 1000; sequence++)
        {
            var reading = Reading(Timestamp.AddSeconds(random.Next(100)));
            tree.Add(sequence, reading);
            expected.Add((reading.Timestamp, sequence), reading);
            if (sequence % 3 == 0)
            {
                var key = expected.Keys.ElementAt(random.Next(expected.Count));
                Assert.True(tree.Remove(key.Timestamp, key.Sequence));
                expected.Remove(key);
            }

            var matches = expected.Values.Where(value => value.Timestamp >= Timestamp.AddSeconds(25) &&
                value.Timestamp <= Timestamp.AddSeconds(75)).ToArray();
            var result = tree.GetRange(Timestamp.AddSeconds(25), Timestamp.AddSeconds(75), 10);
            Assert.Equal(matches.TakeLast(10), result.Readings);
            Assert.Equal(matches.Length > 10, result.HasMore);
            Assert.Equal(expected.Count, tree.Count);
            AssertHeight(tree);
        }

        Assert.Equal(expected.Values, tree.GetReadings());
    }

    private static void AssertHeight(TelemetryHistoryTree tree)
    {
        if (tree.Count == 0)
        {
            Assert.Equal(0, tree.Height);
            return;
        }

        var maximumHeight = (int)Math.Floor(1.4405 * Math.Log2(tree.Count + 2) - 0.328);
        Assert.InRange(tree.Height, 1, maximumHeight);
    }

    private static TelemetryReadingResponse Reading(DateTimeOffset timestamp)
    {
        return new TelemetryReadingResponse(
            Guid.NewGuid(), Guid.NewGuid(), "Environmental", "23.5", "°C",
            timestamp, false, null, 10, 35);
    }
}
