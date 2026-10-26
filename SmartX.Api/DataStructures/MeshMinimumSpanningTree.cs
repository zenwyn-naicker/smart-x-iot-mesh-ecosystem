using SmartX.Api.Models;

namespace SmartX.Api.DataStructures;

public static class MeshMinimumSpanningTree
{
    public static MeshSpanningTree Prim(MeshGraph graph, Guid startNodeId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.Nodes.TryGetValue(startNodeId, out var start))
        {
            throw new KeyNotFoundException("The selected mesh node does not exist.");
        }

        var visited = new HashSet<Guid>();
        var reachable = new List<Guid>();
        var edges = new List<MeshTreeEdge>();
        var candidates = new PriorityQueue<MeshTreeEdge, (double Cost, Guid From, Guid To)>();
        var totalCost = 0d;
        if (start.IsAvailable)
        {
            Visit(startNodeId);
        }

        while (candidates.TryDequeue(out var edge, out _))
        {
            if (visited.Contains(edge.ToNodeId))
            {
                continue;
            }

            edges.Add(edge);
            totalCost += edge.Cost;
            Visit(edge.ToNodeId);
        }

        return new MeshSpanningTree(startNodeId, edges.AsReadOnly(), reachable.AsReadOnly(),
            graph.Nodes.Keys.Where(id => !visited.Contains(id)).ToList().AsReadOnly(), totalCost);

        void Visit(Guid nodeId)
        {
            visited.Add(nodeId);
            reachable.Add(nodeId);
            foreach (var connection in graph.Adjacency[nodeId])
            {
                if (graph.Nodes[connection.NodeId].IsAvailable && !visited.Contains(connection.NodeId))
                {
                    var candidate = new MeshTreeEdge(nodeId, connection.NodeId, connection.Cost);
                    candidates.Enqueue(candidate, (candidate.Cost, candidate.FromNodeId, candidate.ToNodeId));
                }
            }
        }
    }
}
