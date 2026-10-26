using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.DataStructures;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class DemoDataServiceTests
{
    [Fact]
    public async Task SeedCreatesRepeatableMeshWithAlternativeRoutesAndPreservesExistingLinks()
    {
        using var handler = new DeviceApiHandler();
        var mesh = new InMemoryMeshRepository();
        var service = new DemoDataService(new TestClientFactory(handler), mesh);
        await service.SeedAsync(1, CancellationToken.None);
        var graph = mesh.GetGraph();
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(10, graph.Adjacency.Values.Sum(links => links.Count));
        var climate = graph.Nodes.Values.Single(node => node.Identifier == "ESP32-ENV-01").Id;
        var meter = graph.Nodes.Values.Single(node => node.Identifier == "ESP32-PWR-01").Id;
        var relay = graph.Nodes.Values.Single(node => node.Identifier == "ESP32-ACT-01").Id;
        Assert.Equal(new[] { new MeshConnection(climate, 1), new MeshConnection(meter, 8) },
            graph.Adjacency[mesh.GatewayId]);
        Assert.Contains(new MeshConnection(relay, 2), graph.Adjacency[climate]);
        Assert.Contains(new MeshConnection(meter, 3), graph.Adjacency[relay]);
        Assert.Contains(new MeshConnection(meter, 6), graph.Adjacency[climate]);

        var nodes = graph.Nodes.ToDictionary(pair => pair.Key,
            pair => pair.Value with { IsAvailable = true });
        var available = graph with { Nodes = nodes };
        var tree = MeshMinimumSpanningTree.Prim(available, mesh.GatewayId);
        Assert.Equal(6, tree.TotalCost);
        Assert.Equal(new[] { meter, relay, climate, mesh.GatewayId },
            MeshRouting.RouteToRoot(tree, meter).NodeIds);
        nodes[relay] = nodes[relay] with { IsAvailable = false };
        var rerouted = MeshRouting.RouteToRoot(
            MeshMinimumSpanningTree.Prim(available, mesh.GatewayId), meter);
        Assert.Equal(new[] { meter, climate, mesh.GatewayId }, rerouted.NodeIds);
        Assert.Equal(7, rerouted.TotalCost);
        nodes[climate] = nodes[climate] with { IsAvailable = false };
        var direct = MeshRouting.RouteToRoot(
            MeshMinimumSpanningTree.Prim(available, mesh.GatewayId), meter);
        Assert.Equal(new[] { meter, mesh.GatewayId }, direct.NodeIds);
        Assert.Equal(8, direct.TotalCost);

        var custom = Guid.NewGuid();
        mesh.AddNode(new MeshNode(custom, "CUSTOM-01"));
        mesh.AddLink(mesh.GatewayId, custom, 4);
        mesh.UpdateLink(climate, meter, 12);
        await service.SeedAsync(1, CancellationToken.None);
        var repeated = mesh.GetGraph();
        Assert.Equal(3, handler.Registrations);
        Assert.Equal(5, repeated.Nodes.Count);
        Assert.Equal(12, repeated.Adjacency.Values.Sum(links => links.Count));
        Assert.Contains(new MeshConnection(meter, 12), repeated.Adjacency[climate]);
        Assert.Equal(new MeshConnection(mesh.GatewayId, 4), Assert.Single(repeated.Adjacency[custom]));
        Assert.All(graph.Nodes.Keys, id => Assert.Contains(id, repeated.Nodes.Keys));

        mesh.RemoveLink(climate, relay);
        await service.SendLiveAsync(1, CancellationToken.None);
        Assert.DoesNotContain(mesh.GetGraph().Adjacency[climate], link => link.NodeId == relay);
    }

    [Fact]
    public async Task SeedRegistersDevicesAndPostsTypedHistoricalReadingsOverHttp()
    {
        using var handler = new DeviceApiHandler();
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());

        var result = await service.SeedAsync(250, CancellationToken.None);

        Assert.Equal(new SimulationSeedResult(3, 750), result);
        Assert.Equal(3, handler.Registrations);
        Assert.Equal(750, handler.Packets.Count);
        Assert.Contains(handler.Packets, packet => packet.Route.EndsWith("environmental") && packet.Value.GetSingle() == 44f);
        Assert.Contains(handler.Packets, packet => packet.Route.EndsWith("power") && packet.Value.GetInt32() == 4400);
        Assert.All(handler.Packets.Where(packet => packet.Route.EndsWith("actuator")),
            packet => Assert.True(packet.Value.ValueKind is JsonValueKind.True or JsonValueKind.False));
    }

    [Fact]
    public async Task LiveReadingsUseCurrentTimestampsAndReuseDemoRegistrations()
    {
        using var handler = new DeviceApiHandler();
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());
        var start = DateTimeOffset.UtcNow;

        await service.SendLiveAsync(2, CancellationToken.None);
        await service.SendLiveAsync(1, CancellationToken.None);

        Assert.Equal(3, handler.Registrations);
        Assert.Equal(9, handler.Packets.Count);
        Assert.All(handler.Packets, packet => Assert.InRange(packet.Timestamp, start, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task RejectedPacketStopsSendingAndReportsFailure()
    {
        using var handler = new DeviceApiHandler { RejectPackets = true };
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.SendLiveAsync(5, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Single(handler.Packets);
    }

    [Fact]
    public async Task LiveSimulationStopsAndReportsRejectedRequests()
    {
        using var handler = new DeviceApiHandler { RejectPackets = true };
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());
        using var worker = new SimulationWorker(service,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SimulationWorker>.Instance,
            new SmartX.Api.DataStructures.TelemetryQueue(100), new InMemorySensorRepository());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);

        try
        {
            await worker.StartSimulationAsync(1, timeout.Token);

            while (worker.GetStatus().IsRunning)
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.NotNull(worker.GetStatus().Error);
            Assert.Single(handler.Packets);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NormalScenarioUsesRepeatableValuesAndTheSameMeterRelayPair()
    {
        using var handler = new DeviceApiHandler();
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());

        var first = await service.SendScenarioAsync(SimulationScenario.Normal, CancellationToken.None);
        var second = await service.SendScenarioAsync(SimulationScenario.Normal, CancellationToken.None);

        Assert.Equal(first.MeterId, second.MeterId);
        Assert.Equal(first.RelayId, second.RelayId);
        Assert.Equal(3, handler.Registrations);
        Assert.Equal(new[] { "22", "1200", "1200", "true" },
            handler.Packets.Take(4).Select(packet => packet.Value.GetRawText()));
        Assert.Equal(handler.Packets.Take(4).Select(packet => packet.Value.GetRawText()),
            handler.Packets.Skip(4).Select(packet => packet.Value.GetRawText()));
        Assert.Equal(4, first.PacketIds.Distinct().Count());
    }

    [Fact]
    public async Task DisconnectedRelayIsExcludedFromLiveTraffic()
    {
        using var handler = new DeviceApiHandler();
        var service = new DemoDataService(new TestClientFactory(handler), new InMemoryMeshRepository());

        var result = await service.SendLiveAsync(2, CancellationToken.None, relayDisconnected: true);

        Assert.Equal(new SimulationSeedResult(2, 4), result);
        Assert.DoesNotContain(handler.Packets, packet => packet.Route.EndsWith("actuator"));
    }

    private sealed class TestClientFactory(DeviceApiHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("SimulatedDevices", name);
            return new HttpClient(handler, false) { BaseAddress = new Uri("http://localhost/") };
        }
    }

    private sealed class DeviceApiHandler : HttpMessageHandler
    {
        private readonly List<SensorResponse> _sensors = [];
        public int Registrations { get; private set; }
        public bool RejectPackets { get; init; }
        public List<(string Route, JsonElement Value, DateTimeOffset Timestamp)> Packets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && route == "/api/sensors")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(_sensors) };
            }

            if (request.Method == HttpMethod.Post && route == "/api/sensors")
            {
                var registration = (await request.Content!.ReadFromJsonAsync<RegisterSensorRequest>(cancellationToken))!;
                var sensor = SensorResponse.FromSensor(new Sensor
                {
                    DeviceIdentifier = registration.DeviceIdentifier, Facility = registration.Facility,
                    Zone = registration.Zone, SubZone = registration.SubZone, Node = registration.Node,
                    Category = registration.Category
                });
                _sensors.Add(sensor);
                Registrations++;
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(sensor) };
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.StartsWith("/api/telemetry/", route);
            var packet = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Contains(_sensors, sensor => sensor.Id == packet.GetProperty("sensorId").GetGuid());
            Packets.Add((route, packet.GetProperty("value").Clone(), packet.GetProperty("timestamp").GetDateTimeOffset()));
            return new HttpResponseMessage(RejectPackets ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new TelemetryAcceptanceResponse(
                    packet.GetProperty("sensorId").GetGuid(), DateTimeOffset.UtcNow,
                    "Telemetry accepted for processing.", Guid.NewGuid()))
            };
        }
    }
}
