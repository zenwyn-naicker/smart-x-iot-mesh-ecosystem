using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class MeshMinimumSpanningTreeTests
{
    [Fact]
    public void PrimSelectsMinimumCostLinksWithoutCyclesOrChangingTheGraph()
    {
        var ids = CreateIds(5);
        var graph = CreateGraph(ids, (0, 1, 10), (0, 2, 1), (1, 2, 2),
            (1, 3, 1), (2, 3, 8), (2, 4, 9), (3, 4, 3));
        var result = MeshMinimumSpanningTree.Prim(graph, ids[0]);

        Assert.Equal(ids[0], result.StartNodeId);
        Assert.Equal(7, result.TotalCost);
        Assert.Equal(new[]
        {
            new MeshTreeEdge(ids[0], ids[2], 1),
            new MeshTreeEdge(ids[2], ids[1], 2),
            new MeshTreeEdge(ids[1], ids[3], 1),
            new MeshTreeEdge(ids[3], ids[4], 3)
        }, result.Edges);
        Assert.Equal(5, result.ReachableNodeIds.Distinct().Count());
        Assert.Empty(result.UnreachableNodeIds);
        Assert.Equal(14, graph.Adjacency.Values.Sum(links => links.Count));
        Assert.Throws<NotSupportedException>(() => ((IList<MeshTreeEdge>)result.Edges).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)result.ReachableNodeIds).Clear());
        Assert.Equal(7, MeshMinimumSpanningTree.Prim(graph, ids[4]).TotalCost);
    }

    [Fact]
    public void OfflineNodesUseAnAvailableAlternativeAndDisconnectedNodesStayOutsideTheTree()
    {
        var ids = CreateIds(6);
        var graph = CreateGraph(ids, (0, 1, 1), (1, 2, 1), (0, 3, 4), (3, 2, 2), (4, 5, 1));
        var nodes = graph.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        nodes[ids[1]] = nodes[ids[1]] with { IsAvailable = false };
        graph = graph with { Nodes = nodes };
        var result = MeshMinimumSpanningTree.Prim(graph, ids[0]);

        Assert.Equal(6, result.TotalCost);
        Assert.Equal(new[] { ids[0], ids[3], ids[2] }, result.ReachableNodeIds);
        Assert.Equal(new[] { ids[1], ids[4], ids[5] }, result.UnreachableNodeIds);
        Assert.Equal(2, result.Edges.Count);

        nodes[ids[3]] = nodes[ids[3]] with { IsAvailable = false };
        var isolated = MeshMinimumSpanningTree.Prim(graph, ids[0]);
        Assert.Empty(isolated.Edges);
        Assert.Equal(0, isolated.TotalCost);
        Assert.Equal(ids[0], Assert.Single(isolated.ReachableNodeIds));
        Assert.Equal(ids.Skip(1), isolated.UnreachableNodeIds);
        var offline = MeshMinimumSpanningTree.Prim(graph, ids[1]);
        Assert.Empty(offline.Edges);
        Assert.Empty(offline.ReachableNodeIds);
        Assert.Equal(ids, offline.UnreachableNodeIds);
        Assert.Equal(0, offline.TotalCost);
    }

    [Fact]
    public void EqualCostChoicesAreStableAndTrivialOrInvalidStartsAreHandled()
    {
        var ids = CreateIds(4);
        var links = new[] { (0, 2, 1d), (2, 3, 1d), (0, 1, 1d), (1, 3, 1d), (1, 2, 1d) };
        var result = MeshMinimumSpanningTree.Prim(CreateGraph(ids, links), ids[0]);
        var reordered = MeshMinimumSpanningTree.Prim(CreateGraph(ids, links.Reverse().ToArray()), ids[0]);
        Assert.Equal(result.Edges, reordered.Edges);
        Assert.Equal(3, result.TotalCost);
        Assert.Equal(3, result.Edges.Count);
        Assert.Equal(4, result.ReachableNodeIds.Distinct().Count());
        Assert.Empty(result.UnreachableNodeIds);

        var single = CreateGraph([ids[0]]);
        var isolated = MeshMinimumSpanningTree.Prim(single, ids[0]);
        Assert.Empty(isolated.Edges);
        Assert.Empty(isolated.UnreachableNodeIds);
        Assert.Equal(ids[0], Assert.Single(isolated.ReachableNodeIds));
        Assert.Equal(0, isolated.TotalCost);
        Assert.Throws<KeyNotFoundException>(() => MeshMinimumSpanningTree.Prim(single, ids[1]));
        Assert.Throws<KeyNotFoundException>(() => MeshMinimumSpanningTree.Prim(CreateGraph([]), Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => MeshMinimumSpanningTree.Prim(null!, ids[0]));
    }

    private static Guid[] CreateIds(int count)
    {
        return Enumerable.Range(1, count)
            .Select(value => Guid.Parse($"00000000-0000-0000-0000-{value:D12}")).ToArray();
    }

    private static MeshGraph CreateGraph(Guid[] ids, params (int First, int Second, double Cost)[] links)
    {
        var nodes = ids.ToDictionary(id => id, id => new MeshNode(id, id.ToString()) { IsAvailable = true });
        var adjacency = ids.ToDictionary(id => id, _ => new List<MeshConnection>());
        foreach (var (first, second, cost) in links)
        {
            adjacency[ids[first]].Add(new MeshConnection(ids[second], cost));
            adjacency[ids[second]].Add(new MeshConnection(ids[first], cost));
        }

        return new MeshGraph(nodes, adjacency.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<MeshConnection>)pair.Value.AsReadOnly()));
    }
}
