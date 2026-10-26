using SmartX.Api.Models;

namespace SmartX.Api.DataStructures;

public static class MeshTraversal
{
    public static MeshReachability BreadthFirstSearch(MeshGraph graph, Guid startNodeId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.Nodes.TryGetValue(startNodeId, out var start))
        {
            throw new KeyNotFoundException("The selected mesh node does not exist.");
        }

        var visited = new HashSet<Guid>();
        var pending = new Queue<Guid>();
        var reachable = new List<Guid>();
        if (start.IsAvailable)
        {
            visited.Add(startNodeId);
            pending.Enqueue(startNodeId);
        }

        while (pending.TryDequeue(out var nodeId))
        {
            reachable.Add(nodeId);
            foreach (var connection in graph.Adjacency[nodeId])
            {
                if (graph.Nodes[connection.NodeId].IsAvailable && visited.Add(connection.NodeId))
                {
                    pending.Enqueue(connection.NodeId);
                }
            }
        }

        return new MeshReachability(startNodeId, reachable.AsReadOnly(),
            graph.Nodes.Keys.Where(id => !visited.Contains(id)).ToList().AsReadOnly());
    }
}
