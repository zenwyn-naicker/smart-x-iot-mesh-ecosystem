using System.Collections.ObjectModel;
using SmartX.Api.Models;

namespace SmartX.Api.Repositories;

public sealed class InMemoryMeshRepository : IMeshRepository
{
    private readonly Dictionary<Guid, MeshNode> _nodes = [];
    private readonly Dictionary<Guid, Dictionary<Guid, double>> _adjacency = [];
    private readonly object _sync = new();

    public Guid GatewayId { get; } = new("00000000-0000-0000-0000-000000000001");

    public InMemoryMeshRepository()
    {
        _nodes.Add(GatewayId, new MeshNode(GatewayId, "Smart-X Gateway", true));
        _adjacency.Add(GatewayId, []);
    }

    public bool AddNode(MeshNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Id == Guid.Empty || string.IsNullOrWhiteSpace(node.Identifier) || node.IsGateway)
        {
            throw new ArgumentException("Provide a valid sensor node. The gateway already exists.", nameof(node));
        }

        lock (_sync)
        {
            if (!_nodes.TryAdd(node.Id, node with { Identifier = node.Identifier.Trim() }))
            {
                return false;
            }

            _adjacency.Add(node.Id, []);
            return true;
        }
    }

    public bool AddLink(Guid firstNodeId, Guid secondNodeId, double cost)
    {
        ValidateCost(cost);

        lock (_sync)
        {
            ValidateEndpoints(firstNodeId, secondNodeId);

            if (_adjacency[firstNodeId].ContainsKey(secondNodeId))
            {
                return false;
            }

            _adjacency[firstNodeId].Add(secondNodeId, cost);
            _adjacency[secondNodeId].Add(firstNodeId, cost);
            return true;
        }
    }

    public bool UpdateLink(Guid firstNodeId, Guid secondNodeId, double cost)
    {
        ValidateCost(cost);
        lock (_sync)
        {
            ValidateEndpoints(firstNodeId, secondNodeId);
            if (!_adjacency[firstNodeId].ContainsKey(secondNodeId))
            {
                return false;
            }

            _adjacency[firstNodeId][secondNodeId] = cost;
            _adjacency[secondNodeId][firstNodeId] = cost;
            return true;
        }
    }

    public bool RemoveLink(Guid firstNodeId, Guid secondNodeId)
    {
        lock (_sync)
        {
            ValidateEndpoints(firstNodeId, secondNodeId);
            if (!_adjacency[firstNodeId].Remove(secondNodeId))
            {
                return false;
            }

            _adjacency[secondNodeId].Remove(firstNodeId);
            return true;
        }
    }

    private void ValidateEndpoints(Guid firstNodeId, Guid secondNodeId)
    {
        if (firstNodeId == secondNodeId)
        {
            throw new ArgumentException("A link must connect two different nodes.");
        }

        if (!_nodes.ContainsKey(firstNodeId) || !_nodes.ContainsKey(secondNodeId))
        {
            throw new KeyNotFoundException("Both nodes must exist before changing a link.");
        }
    }

    private static void ValidateCost(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0 || cost > MeshLinkRequest.MaximumCost)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), "Link cost must be greater than zero and no more than 1,000,000.");
        }
    }

    public MeshGraph GetGraph()
    {
        lock (_sync)
        {
            var nodes = new ReadOnlyDictionary<Guid, MeshNode>(new Dictionary<Guid, MeshNode>(_nodes));
            var adjacency = _adjacency.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MeshConnection>)pair.Value
                    .Select(link => new MeshConnection(link.Key, link.Value))
                    .ToList().AsReadOnly());

            return new MeshGraph(nodes,
                new ReadOnlyDictionary<Guid, IReadOnlyList<MeshConnection>>(adjacency));
        }
    }
}
