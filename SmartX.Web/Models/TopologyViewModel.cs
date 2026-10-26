namespace SmartX.Web.Models;

public sealed record MeshNodeViewModel(Guid Id, string Identifier, bool IsGateway,
    bool IsAvailable, DateTimeOffset? LastHeartbeat);

public sealed record MeshConnectionViewModel(Guid NodeId, double Cost);

public sealed record MeshLinkViewModel(Guid FromNodeId, Guid ToNodeId, double Cost);

public sealed record MeshLinkForm(Guid FirstNodeId, Guid SecondNodeId, double Cost);

public sealed record MeshReachabilityViewModel(Guid StartNodeId,
    IReadOnlyList<Guid> ReachableNodeIds, IReadOnlyList<Guid> UnreachableNodeIds);

public sealed record MeshRouteViewModel(Guid StartNodeId, Guid DestinationNodeId,
    IReadOnlyList<Guid> NodeIds, double? TotalCost, bool IsReachable);

public sealed class TopologyViewModel
{
    public string? Search { get; set; }
    public SimulationStatusViewModel? Simulation { get; set; }
    public IReadOnlyList<MeshNodeViewModel> Matches { get; set; } = Array.Empty<MeshNodeViewModel>();
    public MeshRouteViewModel? Route { get; set; }
    public MeshReachabilityViewModel? Reachability { get; set; }

    public IReadOnlyDictionary<Guid, MeshNodeViewModel> Nodes { get; init; } =
        new Dictionary<Guid, MeshNodeViewModel>();
    public IReadOnlyDictionary<Guid, IReadOnlyList<MeshConnectionViewModel>> Adjacency { get; init; } =
        new Dictionary<Guid, IReadOnlyList<MeshConnectionViewModel>>();

    public IReadOnlyList<MeshLinkViewModel> Links => Adjacency
        .SelectMany(pair => pair.Value.Where(link => pair.Key.CompareTo(link.NodeId) < 0)
            .Select(link => new MeshLinkViewModel(pair.Key, link.NodeId, link.Cost)))
        .OrderBy(link => Nodes[link.FromNodeId].Identifier)
        .ThenBy(link => Nodes[link.ToNodeId].Identifier)
        .ToList();
}
