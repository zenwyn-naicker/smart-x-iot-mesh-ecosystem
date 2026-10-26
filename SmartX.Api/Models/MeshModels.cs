namespace SmartX.Api.Models;

public sealed record MeshNode(Guid Id, string Identifier, bool IsGateway = false)
{
    public bool IsAvailable { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
}

public sealed record MeshConnection(Guid NodeId, double Cost);

public sealed record MeshLinkRequest(Guid FirstNodeId, Guid SecondNodeId, double Cost)
{
    public const double MaximumCost = 1_000_000;
}

public sealed record MeshGraph(
    IReadOnlyDictionary<Guid, MeshNode> Nodes,
    IReadOnlyDictionary<Guid, IReadOnlyList<MeshConnection>> Adjacency);

public sealed record MeshReachability(
    Guid StartNodeId,
    IReadOnlyList<Guid> ReachableNodeIds,
    IReadOnlyList<Guid> UnreachableNodeIds);

public sealed record MeshTreeEdge(Guid FromNodeId, Guid ToNodeId, double Cost);

public sealed record MeshSpanningTree(
    Guid StartNodeId,
    IReadOnlyList<MeshTreeEdge> Edges,
    IReadOnlyList<Guid> ReachableNodeIds,
    IReadOnlyList<Guid> UnreachableNodeIds,
    double TotalCost);

public sealed record MeshRoute(
    Guid StartNodeId,
    Guid DestinationNodeId,
    IReadOnlyList<Guid> NodeIds,
    double? TotalCost)
{
    public bool IsReachable => NodeIds.Count > 0;
}
