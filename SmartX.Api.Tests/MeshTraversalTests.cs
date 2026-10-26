using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class MeshTraversalTests
{
    [Fact]
    public void BreadthFirstSearchVisitsLayersOnceAndSeparatesDisconnectedNodes()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var graph = CreateGraph(ids, (0, 1), (0, 2), (1, 3), (2, 3), (3, 4));
        var result = MeshTraversal.BreadthFirstSearch(graph, ids[0]);

        Assert.Equal(ids[0], result.StartNodeId);
        Assert.Equal(ids.Take(5), result.ReachableNodeIds);
        Assert.Equal(ids[5], Assert.Single(result.UnreachableNodeIds));
        Assert.Equal(5, result.ReachableNodeIds.Distinct().Count());
        var fromSensor = MeshTraversal.BreadthFirstSearch(graph, ids[4]);
        Assert.Equal(new[] { ids[4], ids[3], ids[1], ids[2], ids[0] }, fromSensor.ReachableNodeIds);
        Assert.Equal(ids[5], Assert.Single(fromSensor.UnreachableNodeIds));
        Assert.Equal(10, graph.Adjacency.Values.Sum(links => links.Count));
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)result.ReachableNodeIds).Clear());
    }

    [Fact]
    public void OfflineNodesBlockTraversalButAnAvailableAlternativeStillWorks()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var graph = CreateGraph(ids, (0, 1), (1, 2), (0, 3), (3, 2));
        var nodes = graph.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        nodes[ids[1]] = nodes[ids[1]] with { IsAvailable = false };
        graph = graph with { Nodes = nodes };
        var result = MeshTraversal.BreadthFirstSearch(graph, ids[0]);
        Assert.Equal(new[] { ids[0], ids[3], ids[2] }, result.ReachableNodeIds);
        Assert.Equal(ids[1], Assert.Single(result.UnreachableNodeIds));

        nodes[ids[3]] = nodes[ids[3]] with { IsAvailable = false };
        var blocked = MeshTraversal.BreadthFirstSearch(graph, ids[0]);
        Assert.Equal(ids[0], Assert.Single(blocked.ReachableNodeIds));
        Assert.Equal(ids.Skip(1), blocked.UnreachableNodeIds);
        var offlineStart = MeshTraversal.BreadthFirstSearch(graph, ids[1]);
        Assert.Empty(offlineStart.ReachableNodeIds);
        Assert.Equal(ids, offlineStart.UnreachableNodeIds);
    }

    [Fact]
    public void TraversalHandlesIsolatedNodesLongChainsAndUnknownStarts()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray();
        var graph = CreateGraph(ids, Enumerable.Range(0, ids.Length - 1)
            .Select(index => (index, index + 1)).ToArray());
        var result = MeshTraversal.BreadthFirstSearch(graph, ids[0]);
        Assert.Equal(ids, result.ReachableNodeIds);
        Assert.Empty(result.UnreachableNodeIds);
        var isolated = MeshTraversal.BreadthFirstSearch(CreateGraph([ids[0]]), ids[0]);
        Assert.Equal(ids[0], Assert.Single(isolated.ReachableNodeIds));
        Assert.Empty(isolated.UnreachableNodeIds);
        Assert.Throws<KeyNotFoundException>(() => MeshTraversal.BreadthFirstSearch(graph, Guid.NewGuid()));
        Assert.Throws<KeyNotFoundException>(() => MeshTraversal.BreadthFirstSearch(CreateGraph([]), Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => MeshTraversal.BreadthFirstSearch(null!, ids[0]));
    }

    private static MeshGraph CreateGraph(Guid[] ids, params (int First, int Second)[] links)
    {
        var nodes = ids.ToDictionary(id => id, id => new MeshNode(id, id.ToString()) { IsAvailable = true });
        var adjacency = ids.ToDictionary(id => id, _ => new List<MeshConnection>());
        foreach (var (first, second) in links)
        {
            adjacency[ids[first]].Add(new MeshConnection(ids[second], 1));
            adjacency[ids[second]].Add(new MeshConnection(ids[first], 1));
        }

        return new MeshGraph(nodes, adjacency.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<MeshConnection>)pair.Value.AsReadOnly()));
    }
}
