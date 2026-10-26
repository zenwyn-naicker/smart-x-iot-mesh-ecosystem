using SmartX.Api.DataStructures;
using SmartX.Api.Models;

namespace SmartX.Api.Tests;

public sealed class MeshRoutingTests
{
    [Fact]
    public void RouteFollowsTreeParentsToTheRootAndSumsOnlyItsOwnEdges()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray();
        var edges = Enumerable.Range(0, ids.Length - 1)
            .Select(index => new MeshTreeEdge(ids[index], ids[index + 1], 1.5)).ToArray();
        var tree = new MeshSpanningTree(ids[0], edges.Reverse().ToArray(), ids, [], edges.Sum(edge => edge.Cost));
        var route = MeshRouting.RouteToRoot(tree, ids[^1]);

        Assert.True(route.IsReachable);
        Assert.Equal(ids[^1], route.StartNodeId);
        Assert.Equal(ids[0], route.DestinationNodeId);
        Assert.Equal(ids.Reverse(), route.NodeIds);
        Assert.Equal(2998.5, route.TotalCost);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)route.NodeIds).Clear());

        var partial = MeshRouting.RouteToRoot(tree, ids[2]);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, partial.NodeIds);
        Assert.Equal(3, partial.TotalCost);
        var root = MeshRouting.RouteToRoot(tree, ids[0]);
        Assert.True(root.IsReachable);
        Assert.Equal(ids[0], Assert.Single(root.NodeIds));
        Assert.Equal(0, root.TotalCost);
        Assert.Equal(1999, tree.Edges.Count);

        var adjacency = ids.ToDictionary(id => id, _ => new List<MeshConnection>());
        foreach (var edge in edges)
        {
            adjacency[edge.FromNodeId].Add(new MeshConnection(edge.ToNodeId, edge.Cost));
            adjacency[edge.ToNodeId].Add(new MeshConnection(edge.FromNodeId, edge.Cost));
        }
        var graph = new MeshGraph(
            ids.ToDictionary(id => id, id => new MeshNode(id, id.ToString()) { IsAvailable = true }),
            adjacency.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<MeshConnection>)pair.Value));
        var traversal = MeshTraversal.BreadthFirstSearch(graph, ids[0]);
        Assert.Equal(ids, traversal.ReachableNodeIds);
        Assert.Empty(traversal.UnreachableNodeIds);
        var spanningTree = MeshMinimumSpanningTree.Prim(graph, ids[0]);
        Assert.Equal(edges, spanningTree.Edges);
        Assert.Equal(2998.5, spanningTree.TotalCost);
        Assert.Empty(spanningTree.UnreachableNodeIds);
        Assert.Equal(route.NodeIds, MeshRouting.RouteToRoot(spanningTree, ids[^1]).NodeIds);
    }

    [Fact]
    public void UnreachableNodesHaveNoRouteAndUnknownNodesAreRejected()
    {
        var rootId = Guid.NewGuid();
        var disconnectedId = Guid.NewGuid();
        var tree = new MeshSpanningTree(rootId, [], [rootId], [disconnectedId], 0);
        var route = MeshRouting.RouteToRoot(tree, disconnectedId);

        Assert.False(route.IsReachable);
        Assert.Equal(disconnectedId, route.StartNodeId);
        Assert.Equal(rootId, route.DestinationNodeId);
        Assert.Empty(route.NodeIds);
        Assert.Null(route.TotalCost);
        var offlineRoot = MeshRouting.RouteToRoot(tree with
        {
            ReachableNodeIds = [],
            UnreachableNodeIds = [rootId, disconnectedId]
        }, rootId);
        Assert.False(offlineRoot.IsReachable);
        Assert.Empty(offlineRoot.NodeIds);
        Assert.Null(offlineRoot.TotalCost);
        Assert.Throws<KeyNotFoundException>(() => MeshRouting.RouteToRoot(tree, Guid.NewGuid()));
        Assert.Throws<ArgumentNullException>(() => MeshRouting.RouteToRoot(null!, rootId));
    }
}
