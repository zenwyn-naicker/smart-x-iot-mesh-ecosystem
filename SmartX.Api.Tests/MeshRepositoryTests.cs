using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Tests;

public sealed class MeshRepositoryTests
{
    [Fact]
    public void GraphContainsOneGatewayAndSymmetricWeightedLinks()
    {
        var repository = new InMemoryMeshRepository();
        var initial = repository.GetGraph();
        Assert.True(Assert.Single(initial.Nodes).Value.IsGateway);
        Assert.Empty(initial.Adjacency[repository.GatewayId]);
        var node = new MeshNode(Guid.NewGuid(), " ENV-01 ");
        var isolated = new MeshNode(Guid.NewGuid(), "ENV-02");

        Assert.True(repository.AddNode(node));
        Assert.True(repository.AddNode(isolated));
        Assert.False(repository.AddNode(node with { Identifier = "Replacement" }));
        Assert.True(repository.AddLink(repository.GatewayId, node.Id, 2.5));
        Assert.False(repository.AddLink(node.Id, repository.GatewayId, 8));

        var graph = repository.GetGraph();
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal("ENV-01", graph.Nodes[node.Id].Identifier);
        Assert.Equal(new MeshConnection(node.Id, 2.5), Assert.Single(graph.Adjacency[repository.GatewayId]));
        Assert.Equal(new MeshConnection(repository.GatewayId, 2.5), Assert.Single(graph.Adjacency[node.Id]));
        Assert.Empty(graph.Adjacency[isolated.Id]);
        Assert.Single(initial.Nodes);
        Assert.Empty(initial.Adjacency[repository.GatewayId]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<Guid, MeshNode>)graph.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<MeshConnection>)graph.Adjacency[node.Id]).Clear());

        Assert.True(repository.UpdateLink(node.Id, repository.GatewayId, 4));
        var updated = repository.GetGraph();
        Assert.Equal(new MeshConnection(node.Id, 4), Assert.Single(updated.Adjacency[repository.GatewayId]));
        Assert.Equal(new MeshConnection(repository.GatewayId, 4), Assert.Single(updated.Adjacency[node.Id]));
        Assert.Equal(2.5, Assert.Single(graph.Adjacency[node.Id]).Cost);
        Assert.True(repository.RemoveLink(node.Id, repository.GatewayId));
        Assert.False(repository.RemoveLink(repository.GatewayId, node.Id));
        Assert.False(repository.UpdateLink(repository.GatewayId, node.Id, 5));
        var removed = repository.GetGraph();
        Assert.Equal(3, removed.Nodes.Count);
        Assert.Empty(removed.Adjacency[node.Id]);
        Assert.Empty(removed.Adjacency[repository.GatewayId]);
        Assert.Single(updated.Adjacency[node.Id]);
        Assert.True(repository.AddLink(node.Id, repository.GatewayId, 6));
    }

    [Fact]
    public void InvalidNodesAndLinksLeaveTheGraphUnchanged()
    {
        var repository = new InMemoryMeshRepository();
        var node = new MeshNode(Guid.NewGuid(), "ENV-01");
        Assert.Throws<ArgumentNullException>(() => repository.AddNode(null!));
        Assert.Throws<ArgumentException>(() => repository.AddNode(node with { Id = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => repository.AddNode(node with { Identifier = " " }));
        Assert.Throws<ArgumentException>(() => repository.AddNode(node with { IsGateway = true }));
        Assert.False(repository.AddNode(node with { Id = repository.GatewayId }));
        Assert.True(repository.AddNode(node));
        Assert.Throws<ArgumentException>(() => repository.AddLink(node.Id, node.Id, 1));
        Assert.Throws<KeyNotFoundException>(() => repository.AddLink(node.Id, Guid.NewGuid(), 1));
        Assert.Throws<KeyNotFoundException>(() => repository.UpdateLink(node.Id, Guid.NewGuid(), 1));
        Assert.Throws<KeyNotFoundException>(() => repository.RemoveLink(node.Id, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => repository.UpdateLink(node.Id, node.Id, 1));
        Assert.Throws<ArgumentException>(() => repository.RemoveLink(node.Id, node.Id));

        foreach (var cost in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            MeshLinkRequest.MaximumCost + 1, 1e308 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => repository.AddLink(repository.GatewayId, node.Id, cost));
            Assert.Throws<ArgumentOutOfRangeException>(() => repository.UpdateLink(repository.GatewayId, node.Id, cost));
        }

        var graph = repository.GetGraph();
        Assert.Equal(2, graph.Nodes.Count);
        Assert.All(graph.Adjacency.Values, connections => Assert.Empty(connections));
        repository.AddLink(repository.GatewayId, node.Id, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.UpdateLink(node.Id, repository.GatewayId, double.NaN));
        Assert.Equal(3, Assert.Single(repository.GetGraph().Adjacency[node.Id]).Cost);
    }

    [Fact]
    public void ConcurrentUpdatesKeepSnapshotsConsistent()
    {
        var repository = new InMemoryMeshRepository();
        var nodes = Enumerable.Range(0, 100)
            .Select(index => new MeshNode(Guid.NewGuid(), $"ENV-{index}"))
            .ToArray();

        Parallel.ForEach(nodes, node =>
        {
            Assert.True(repository.AddNode(node));
            Assert.True(repository.AddLink(repository.GatewayId, node.Id, 1));
            Assert.True(repository.UpdateLink(node.Id, repository.GatewayId, 2));
            Assert.True(repository.RemoveLink(repository.GatewayId, node.Id));
            Assert.True(repository.AddLink(node.Id, repository.GatewayId, 1));
            var graph = repository.GetGraph();
            Assert.Equal(graph.Nodes.Count, graph.Adjacency.Count);
            foreach (var (id, connections) in graph.Adjacency)
            {
                foreach (var connection in connections)
                {
                    Assert.Contains(new MeshConnection(id, connection.Cost), graph.Adjacency[connection.NodeId]);
                }
            }
        });

        var result = repository.GetGraph();
        Assert.Equal(101, result.Nodes.Count);
        Assert.Equal(100, result.Adjacency[repository.GatewayId].Count);
        Assert.All(nodes, node => Assert.Single(result.Adjacency[node.Id]));
    }
}
