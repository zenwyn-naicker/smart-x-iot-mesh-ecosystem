using SmartX.Api.Models;

namespace SmartX.Api.Repositories;

public interface IMeshRepository
{
    Guid GatewayId { get; }

    bool AddNode(MeshNode node);

    bool AddLink(Guid firstNodeId, Guid secondNodeId, double cost);

    bool UpdateLink(Guid firstNodeId, Guid secondNodeId, double cost);

    bool RemoveLink(Guid firstNodeId, Guid secondNodeId);

    MeshGraph GetGraph();
}
