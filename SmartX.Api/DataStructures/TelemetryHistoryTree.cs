using SmartX.Api.Models;

namespace SmartX.Api.DataStructures;

public sealed class TelemetryHistoryTree
{
    private Node? _root;

    public int Count { get; private set; }

    public int Height => GetHeight(_root);

    public void Add(long sequence, TelemetryReadingResponse reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        _root = Insert(_root, new Node(sequence, reading));
        Count++;
    }

    public bool Remove(DateTimeOffset timestamp, long sequence)
    {
        var removed = false;
        _root = Remove(_root, (timestamp, sequence), ref removed);
        if (removed)
        {
            Count--;
        }

        return removed;
    }

    public IReadOnlyList<TelemetryReadingResponse> GetReadings()
    {
        var readings = new List<TelemetryReadingResponse>(Count);
        CollectReadings(_root, readings);
        return readings;
    }

    public TelemetryHistoryRangeResponse GetRange(
        DateTimeOffset from, DateTimeOffset to, int limit = 200)
    {
        if (from > to)
        {
            throw new ArgumentException("The start time must not be after the end time.", nameof(from));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var readings = new List<TelemetryReadingResponse>(Math.Min(Count, limit));
        var hasMore = CollectRange(_root, from, to, limit, readings);
        readings.Reverse();
        return new TelemetryHistoryRangeResponse(readings, hasMore);
    }

    private static bool CollectRange(
        Node? node, DateTimeOffset from, DateTimeOffset to, int limit,
        List<TelemetryReadingResponse> readings)
    {
        if (node is null)
        {
            return false;
        }

        if (node.Key.Timestamp > to)
        {
            return CollectRange(node.Left, from, to, limit, readings);
        }

        if (node.Key.Timestamp < from)
        {
            return CollectRange(node.Right, from, to, limit, readings);
        }

        if (CollectRange(node.Right, from, to, limit, readings) || readings.Count == limit)
        {
            return true;
        }

        readings.Add(node.Reading);
        return CollectRange(node.Left, from, to, limit, readings);
    }

    private static Node Insert(Node? node, Node entry)
    {
        if (node is null)
        {
            return entry;
        }

        var comparison = entry.Key.CompareTo(node.Key);
        if (comparison < 0)
        {
            node.Left = Insert(node.Left, entry);
        }
        else if (comparison > 0)
        {
            node.Right = Insert(node.Right, entry);
        }
        else
        {
            throw new ArgumentException("A reading with this timestamp and sequence already exists.");
        }

        return Balance(node);
    }

    private static Node? Remove(
        Node? node, (DateTimeOffset Timestamp, long Sequence) key, ref bool removed)
    {
        if (node is null)
        {
            return null;
        }

        var comparison = key.CompareTo(node.Key);
        if (comparison < 0)
        {
            node.Left = Remove(node.Left, key, ref removed);
        }
        else if (comparison > 0)
        {
            node.Right = Remove(node.Right, key, ref removed);
        }
        else
        {
            removed = true;
            if (node.Left is null)
            {
                return node.Right;
            }

            if (node.Right is null)
            {
                return node.Left;
            }

            var right = RemoveMinimum(node.Right, out var successor);
            successor.Left = node.Left;
            successor.Right = right;
            return Balance(successor);
        }

        return removed ? Balance(node) : node;
    }

    private static Node? RemoveMinimum(Node node, out Node minimum)
    {
        if (node.Left is null)
        {
            minimum = node;
            return node.Right;
        }

        node.Left = RemoveMinimum(node.Left, out minimum);
        return Balance(node);
    }

    private static Node Balance(Node node)
    {
        UpdateHeight(node);
        var balance = GetHeight(node.Left) - GetHeight(node.Right);

        if (balance > 1)
        {
            if (GetHeight(node.Left!.Left) < GetHeight(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left);
            }

            return RotateRight(node);
        }

        if (balance < -1)
        {
            if (GetHeight(node.Right!.Right) < GetHeight(node.Right.Left))
            {
                node.Right = RotateRight(node.Right);
            }

            return RotateLeft(node);
        }

        return node;
    }

    private static Node RotateLeft(Node node)
    {
        var root = node.Right!;
        node.Right = root.Left;
        root.Left = node;
        UpdateHeight(node);
        UpdateHeight(root);
        return root;
    }

    private static Node RotateRight(Node node)
    {
        var root = node.Left!;
        node.Left = root.Right;
        root.Right = node;
        UpdateHeight(node);
        UpdateHeight(root);
        return root;
    }

    private static int GetHeight(Node? node) => node?.Height ?? 0;

    private static void UpdateHeight(Node node)
    {
        node.Height = 1 + Math.Max(GetHeight(node.Left), GetHeight(node.Right));
    }

    private static void CollectReadings(Node? node, List<TelemetryReadingResponse> readings)
    {
        if (node is null)
        {
            return;
        }

        CollectReadings(node.Left, readings);
        readings.Add(node.Reading);
        CollectReadings(node.Right, readings);
    }

    private sealed class Node(long sequence, TelemetryReadingResponse reading)
    {
        public (DateTimeOffset Timestamp, long Sequence) Key { get; } = (reading.Timestamp, sequence);

        public TelemetryReadingResponse Reading { get; } = reading;

        public Node? Left { get; set; }

        public Node? Right { get; set; }

        public int Height { get; set; } = 1;
    }
}
