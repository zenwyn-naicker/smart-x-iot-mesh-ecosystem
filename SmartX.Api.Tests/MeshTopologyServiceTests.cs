using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class MeshTopologyServiceTests
{
    [Fact]
    public void RegisteredSensorsJoinTheGraphAndSupportLinkChanges()
    {
        var sensors = new InMemorySensorRepository();
        var mesh = new InMemoryMeshRepository();
        var service = new MeshTopologyService(sensors, mesh);
        var initial = service.GetGraph();
        Assert.True(Assert.Single(initial.Nodes).Value.IsAvailable);

        var sensor = AddSensor(sensors, "ENV-01");
        var graph = service.GetGraph();
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(sensor.DeviceIdentifier, graph.Nodes[sensor.Id].Identifier);
        Assert.False(graph.Nodes[sensor.Id].IsAvailable);
        Assert.Null(graph.Nodes[sensor.Id].LastHeartbeat);
        Assert.All(graph.Adjacency.Values, links => Assert.Empty(links));
        Assert.Single(initial.Nodes);
        Assert.Equal(2, service.GetGraph().Nodes.Count);

        var second = AddSensor(sensors, "ENV-02");
        Assert.True(service.AddLink(sensor.Id, second.Id, 2));
        Assert.True(service.AddLink(mesh.GatewayId, sensor.Id, 1));
        Assert.False(service.AddLink(second.Id, sensor.Id, 9));
        Assert.True(service.UpdateLink(second.Id, sensor.Id, 3));
        var linked = service.GetGraph();
        Assert.Equal(3, Assert.Single(linked.Adjacency[second.Id]).Cost);
        Assert.Equal(2, linked.Adjacency[sensor.Id].Count);
        Assert.False(linked.Nodes[second.Id].IsAvailable);
        Assert.True(service.RemoveLink(second.Id, sensor.Id));
        Assert.False(service.RemoveLink(sensor.Id, second.Id));
        Assert.False(service.UpdateLink(sensor.Id, second.Id, 4));
        Assert.Equal(new MeshConnection(mesh.GatewayId, 1), Assert.Single(service.GetGraph().Adjacency[sensor.Id]));

        foreach (var operation in new Action<Guid, Guid>[]
        {
            (first, last) => service.AddLink(first, last, 1),
            (first, last) => service.UpdateLink(first, last, 1),
            (first, last) => service.RemoveLink(first, last)
        })
        {
            Assert.Throws<ArgumentException>(() => operation(sensor.Id, sensor.Id));
            Assert.Throws<ArgumentException>(() => operation(Guid.Empty, sensor.Id));
            Assert.Throws<KeyNotFoundException>(() => operation(sensor.Id, Guid.NewGuid()));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => service.AddLink(sensor.Id, second.Id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.UpdateLink(mesh.GatewayId, sensor.Id, double.PositiveInfinity));
        Assert.Equal(1, Assert.Single(service.GetGraph().Adjacency[mesh.GatewayId]).Cost);
        Assert.Empty(service.GetGraph().Adjacency[second.Id]);
    }

    [Fact]
    public void HeartbeatExpiryAndRecoveryPreserveLinksAndEarlierSnapshots()
    {
        var sensors = new InMemorySensorRepository();
        var mesh = new InMemoryMeshRepository();
        var clock = new TestClock();
        var service = new MeshTopologyService(sensors, mesh, clock);
        var sensor = AddSensor(sensors, "ENV-01");
        service.GetGraph();
        mesh.AddLink(mesh.GatewayId, sensor.Id, 2);
        var heartbeat = clock.GetUtcNow();
        sensors.UpdateHeartbeat(sensor.Id, heartbeat);
        clock.Now = heartbeat.AddSeconds(30);
        var boundary = service.GetGraph();
        Assert.True(boundary.Nodes[sensor.Id].IsAvailable);

        clock.Now = clock.Now.AddTicks(1);
        var expired = service.GetGraph();
        Assert.False(expired.Nodes[sensor.Id].IsAvailable);
        Assert.Equal(heartbeat, expired.Nodes[sensor.Id].LastHeartbeat);
        Assert.True(expired.Nodes[mesh.GatewayId].IsAvailable);
        Assert.Single(expired.Adjacency[sensor.Id]);
        Assert.True(boundary.Nodes[sensor.Id].IsAvailable);

        var issues = new ActiveIssueService();
        var telemetry = new TelemetryService(sensors, issues,
            new DeviceCommandService(sensors, new UserActivityService(sensors, issues)));
        telemetry.IngestEnvironmental(new(sensor.Id, 44f, clock.Now, "C"));
        var recovered = service.GetGraph();
        Assert.True(recovered.Nodes[sensor.Id].IsAvailable);
        Assert.Equal(clock.Now, recovered.Nodes[sensor.Id].LastHeartbeat);
        Assert.Single(issues.GetActiveIssues());
        Assert.Equal(new MeshConnection(mesh.GatewayId, 2), Assert.Single(recovered.Adjacency[sensor.Id]));
        Assert.False(expired.Nodes[sensor.Id].IsAvailable);

        telemetry.IngestEnvironmental(new(sensor.Id, 22f, heartbeat.AddMinutes(-1), "C"));
        Assert.Equal(clock.Now, service.GetGraph().Nodes[sensor.Id].LastHeartbeat);
    }

    [Fact]
    public void AvailabilityIsIndependentForEachSensor()
    {
        var sensors = new InMemorySensorRepository();
        var mesh = new InMemoryMeshRepository();
        var clock = new TestClock();
        var service = new MeshTopologyService(sensors, mesh, clock);
        var active = AddSensor(sensors, "ENV-01");
        var expired = AddSensor(sensors, "ENV-02");
        var silent = AddSensor(sensors, "ENV-03");
        sensors.UpdateHeartbeat(active.Id, clock.Now.AddSeconds(-5));
        sensors.UpdateHeartbeat(expired.Id, clock.Now.AddSeconds(-31));
        var graph = service.GetGraph();

        Assert.True(graph.Nodes[active.Id].IsAvailable);
        Assert.False(graph.Nodes[expired.Id].IsAvailable);
        Assert.False(graph.Nodes[silent.Id].IsAvailable);
        Assert.True(graph.Nodes[mesh.GatewayId].IsAvailable);
        Assert.Equal(4, graph.Nodes.Count);
        service.AddLink(mesh.GatewayId, active.Id, 1);
        service.AddLink(active.Id, expired.Id, 1);
        service.AddLink(expired.Id, silent.Id, 1);
        var reachability = service.GetReachability(mesh.GatewayId);
        Assert.Equal(new[] { mesh.GatewayId, active.Id }, reachability.ReachableNodeIds);
        Assert.Contains(expired.Id, reachability.UnreachableNodeIds);
        Assert.Contains(silent.Id, reachability.UnreachableNodeIds);
        var tree = service.GetMinimumSpanningTree(mesh.GatewayId);
        Assert.Equal(new MeshTreeEdge(mesh.GatewayId, active.Id, 1), Assert.Single(tree.Edges));
        Assert.Equal(reachability.UnreachableNodeIds, tree.UnreachableNodeIds);

        sensors.UpdateHeartbeat(expired.Id, clock.Now);
        sensors.UpdateHeartbeat(silent.Id, clock.Now);
        var recovered = service.GetReachability(mesh.GatewayId);
        Assert.Equal(4, recovered.ReachableNodeIds.Count);
        Assert.Empty(recovered.UnreachableNodeIds);
        var recoveredTree = service.GetMinimumSpanningTree(mesh.GatewayId);
        Assert.Equal(3, recoveredTree.Edges.Count);
        Assert.Equal(3, recoveredTree.TotalCost);
        Assert.Empty(recoveredTree.UnreachableNodeIds);
    }

    [Fact]
    public void GatewayRoutesFollowTheTreeAndRecalculateAfterFailuresAndLinkChanges()
    {
        var sensors = new InMemorySensorRepository();
        var mesh = new InMemoryMeshRepository();
        var clock = new TestClock();
        var service = new MeshTopologyService(sensors, mesh, clock);
        var first = AddSensor(sensors, "ENV-01");
        var target = AddSensor(sensors, "ENV-02");
        var alternative = AddSensor(sensors, "ENV-03");
        var isolated = AddSensor(sensors, "ENV-04");
        foreach (var sensor in new[] { first, target, alternative, isolated })
        {
            sensors.UpdateHeartbeat(sensor.Id, clock.Now);
        }

        service.AddLink(mesh.GatewayId, first.Id, 1);
        service.AddLink(first.Id, target.Id, 1);
        service.AddLink(mesh.GatewayId, alternative.Id, 4);
        service.AddLink(alternative.Id, target.Id, 2);
        service.AddLink(mesh.GatewayId, target.Id, 5);
        var original = service.GetGatewayRoute(target.Id);
        Assert.Equal(new[] { target.Id, first.Id, mesh.GatewayId }, original.NodeIds);
        Assert.Equal(2, original.TotalCost);

        clock.Now = clock.Now.AddSeconds(31);
        sensors.UpdateHeartbeat(target.Id, clock.Now);
        sensors.UpdateHeartbeat(alternative.Id, clock.Now);
        sensors.UpdateHeartbeat(isolated.Id, clock.Now);
        var rerouted = service.GetGatewayRoute(target.Id);
        Assert.True(rerouted.IsReachable);
        Assert.Equal(new[] { target.Id, alternative.Id, mesh.GatewayId }, rerouted.NodeIds);
        Assert.Equal(6, rerouted.TotalCost);
        Assert.Equal(2, original.TotalCost);
        Assert.False(service.GetGatewayRoute(first.Id).IsReachable);
        Assert.False(service.GetGatewayRoute(isolated.Id).IsReachable);

        service.UpdateLink(mesh.GatewayId, target.Id, 0.5);
        var direct = service.GetGatewayRoute(target.Id);
        Assert.Equal(new[] { target.Id, mesh.GatewayId }, direct.NodeIds);
        Assert.Equal(0.5, direct.TotalCost);
        service.RemoveLink(mesh.GatewayId, target.Id);
        service.RemoveLink(mesh.GatewayId, alternative.Id);
        var disconnected = service.GetGatewayRoute(target.Id);
        Assert.False(disconnected.IsReachable);
        Assert.Empty(disconnected.NodeIds);
        Assert.Null(disconnected.TotalCost);

        sensors.UpdateHeartbeat(first.Id, clock.Now);
        Assert.Equal(original.NodeIds, service.GetGatewayRoute(target.Id).NodeIds);
        var gateway = service.GetGatewayRoute(mesh.GatewayId);
        Assert.Equal(mesh.GatewayId, Assert.Single(gateway.NodeIds));
        Assert.Equal(0, gateway.TotalCost);
        Assert.Throws<KeyNotFoundException>(() => service.GetGatewayRoute(Guid.NewGuid()));
    }

    private static Sensor AddSensor(InMemorySensorRepository repository, string identifier)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = identifier,
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = identifier,
            Category = SensorCategory.Environmental
        };
        repository.Add(sensor);
        return sensor;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
