using System.Collections.ObjectModel;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class MeshTopologyService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly IMeshRepository _meshRepository;
    private readonly TimeProvider _timeProvider;

    public MeshTopologyService(
        ISensorRepository sensorRepository,
        IMeshRepository meshRepository,
        TimeProvider? timeProvider = null)
    {
        _sensorRepository = sensorRepository;
        _meshRepository = meshRepository;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MeshGraph GetGraph()
    {
        var heartbeats = new Dictionary<Guid, DateTimeOffset?>();
        foreach (var sensor in _sensorRepository.GetAll())
        {
            _meshRepository.AddNode(new MeshNode(sensor.Id, sensor.DeviceIdentifier));
            heartbeats.Add(sensor.Id, sensor.LastHeartbeat);
        }

        var now = _timeProvider.GetUtcNow();
        var graph = _meshRepository.GetGraph();
        var nodes = graph.Nodes.ToDictionary(pair => pair.Key, pair =>
        {
            var heartbeat = heartbeats.GetValueOrDefault(pair.Key);
            return pair.Value with
            {
                LastHeartbeat = heartbeat,
                IsAvailable = pair.Value.IsGateway ||
                    heartbeat is not null && now - heartbeat.Value <= TimeSpan.FromSeconds(30)
            };
        });

        return graph with { Nodes = new ReadOnlyDictionary<Guid, MeshNode>(nodes) };
    }

    public bool AddLink(Guid firstNodeId, Guid secondNodeId, double cost)
    {
        EnsureEndpoints(firstNodeId, secondNodeId);
        return _meshRepository.AddLink(firstNodeId, secondNodeId, cost);
    }

    public MeshReachability GetReachability(Guid startNodeId)
    {
        return MeshTraversal.BreadthFirstSearch(GetGraph(), startNodeId);
    }

    public MeshSpanningTree GetMinimumSpanningTree(Guid startNodeId)
    {
        return MeshMinimumSpanningTree.Prim(GetGraph(), startNodeId);
    }

    public MeshRoute GetGatewayRoute(Guid startNodeId)
    {
        var tree = GetMinimumSpanningTree(_meshRepository.GatewayId);
        return MeshRouting.RouteToRoot(tree, startNodeId);
    }

    public bool UpdateLink(Guid firstNodeId, Guid secondNodeId, double cost)
    {
        EnsureEndpoints(firstNodeId, secondNodeId);
        return _meshRepository.UpdateLink(firstNodeId, secondNodeId, cost);
    }

    public bool RemoveLink(Guid firstNodeId, Guid secondNodeId)
    {
        EnsureEndpoints(firstNodeId, secondNodeId);
        return _meshRepository.RemoveLink(firstNodeId, secondNodeId);
    }

    private void EnsureEndpoints(Guid firstNodeId, Guid secondNodeId)
    {
        if (firstNodeId == Guid.Empty || secondNodeId == Guid.Empty || firstNodeId == secondNodeId)
        {
            throw new ArgumentException("Select two different nodes.");
        }

        var first = GetEndpoint(firstNodeId);
        var second = GetEndpoint(secondNodeId);
        if (first is not null)
        {
            _meshRepository.AddNode(new MeshNode(first.Id, first.DeviceIdentifier));
        }

        if (second is not null)
        {
            _meshRepository.AddNode(new MeshNode(second.Id, second.DeviceIdentifier));
        }
    }

    private Sensor? GetEndpoint(Guid id)
    {
        return id == _meshRepository.GatewayId
            ? null
            : _sensorRepository.GetById(id)
                ?? throw new KeyNotFoundException("The selected sensor does not exist.");
    }
}
