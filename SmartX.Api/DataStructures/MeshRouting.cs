using SmartX.Api.Models;

namespace SmartX.Api.DataStructures;

public static class MeshRouting
{
    public static MeshRoute RouteToRoot(MeshSpanningTree tree, Guid startNodeId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (tree.UnreachableNodeIds.Contains(startNodeId))
        {
            return new MeshRoute(startNodeId, tree.StartNodeId, Array.Empty<Guid>(), null);
        }

        if (!tree.ReachableNodeIds.Contains(startNodeId))
        {
            throw new KeyNotFoundException("The selected mesh node does not exist.");
        }

        var parents = tree.Edges.ToDictionary(edge => edge.ToNodeId);
        var path = new List<Guid> { startNodeId };
        var nodeId = startNodeId;
        var totalCost = 0d;
        while (nodeId != tree.StartNodeId)
        {
            var edge = parents[nodeId];
            totalCost += edge.Cost;
            nodeId = edge.FromNodeId;
            path.Add(nodeId);
        }

        return new MeshRoute(startNodeId, tree.StartNodeId, path.AsReadOnly(), totalCost);
    }
}
