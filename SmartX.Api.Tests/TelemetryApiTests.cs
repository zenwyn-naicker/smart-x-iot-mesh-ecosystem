using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartX.Api.Controllers;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class TelemetryApiTests : IAsyncLifetime
{
    private readonly InMemorySensorRepository _repository = new();
    private readonly TelemetryQueue _queue = new(32);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(TelemetryController).Assembly);
        builder.Services.AddSingleton<ISensorRepository>(_repository);
        builder.Services.AddSingleton<IMeshRepository, InMemoryMeshRepository>();
        builder.Services.AddSingleton<MeshTopologyService>();
        builder.Services.AddSingleton(_queue);
        builder.Services.AddSingleton<ActiveIssueService>();
        builder.Services.AddSingleton<TelemetryService>();
        builder.Services.AddSingleton<TelemetryProcessingWorker>();
        builder.Services.AddSingleton<SensorService>();
        builder.Services.AddSingleton<DeviceCommandService>();
        builder.Services.AddSingleton<UserActivityService>();
        builder.Services.AddSingleton<ActionPatternService>();
        builder.Services.AddSingleton<ActionSuggestionService>();
        builder.Services.AddSingleton<DeploymentTreeValidator>();
        builder.Services.AddSingleton<DemoDataService>();
        builder.Services.AddSingleton<SimulationWorker>();
        builder.Services.AddSingleton<DashboardService>();
        builder.Services.AddSingleton<CommandStreamService>();
        builder.Services.AddHttpClient("SimulatedDevices", client =>
        {
            client.BaseAddress = new Uri(_app.Urls.Single());
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _client = new HttpClient
        {
            BaseAddress = new Uri(_app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task TopologyEndpointsReturnGraphTraversalTreeAndGatewayRoutes()
    {
        var mesh = _app.Services.GetRequiredService<IMeshRepository>();
        var topology = _app.Services.GetRequiredService<MeshTopologyService>();
        var initial = await _client.GetFromJsonAsync<MeshGraph>("/api/topology");
        Assert.Equal(mesh.GatewayId, Assert.Single(initial!.Nodes).Key);
        var first = AddSensor(SensorCategory.Environmental);
        var second = AddSensor(SensorCategory.PowerConsumption);
        var isolated = AddSensor(SensorCategory.Actuator);
        _repository.UpdateHeartbeat(first, DateTimeOffset.UtcNow);
        _repository.UpdateHeartbeat(second, DateTimeOffset.UtcNow);
        topology.AddLink(mesh.GatewayId, first, 2);
        topology.AddLink(first, second, 3);
        topology.AddLink(mesh.GatewayId, second, 9);

        var graph = await _client.GetFromJsonAsync<MeshGraph>("/api/topology");
        Assert.Equal(4, graph!.Nodes.Count);
        Assert.True(graph.Nodes[mesh.GatewayId].IsGateway);
        Assert.True(graph.Nodes[first].IsAvailable);
        Assert.False(graph.Nodes[isolated].IsAvailable);
        Assert.Contains(new MeshConnection(first, 2), graph.Adjacency[mesh.GatewayId]);
        var reachable = await _client.GetFromJsonAsync<MeshReachability>(
            $"/api/topology/reachability?startNodeId={mesh.GatewayId}");
        Assert.Equal(3, reachable!.ReachableNodeIds.Count);
        Assert.Equal(isolated, Assert.Single(reachable.UnreachableNodeIds));
        var tree = await _client.GetFromJsonAsync<MeshSpanningTree>(
            $"/api/topology/spanning-tree?startNodeId={mesh.GatewayId}");
        Assert.Equal(5, tree!.TotalCost);
        Assert.Equal(2, tree.Edges.Count);
        Assert.Equal(isolated, Assert.Single(tree.UnreachableNodeIds));
        var route = await _client.GetFromJsonAsync<MeshRoute>(
            $"/api/topology/route?startNodeId={second}");
        Assert.True(route!.IsReachable);
        Assert.Equal(new[] { second, first, mesh.GatewayId }, route.NodeIds);
        Assert.Equal(5, route.TotalCost);

        var offline = await _client.GetFromJsonAsync<MeshRoute>(
            $"/api/topology/route?startNodeId={isolated}");
        Assert.False(offline!.IsReachable);
        Assert.Empty(offline.NodeIds);
        Assert.Null(offline.TotalCost);
        _repository.UpdateHeartbeat(isolated, DateTimeOffset.UtcNow);
        var disconnected = await _client.GetFromJsonAsync<MeshRoute>(
            $"/api/topology/route?startNodeId={isolated}");
        Assert.False(disconnected!.IsReachable);
        Assert.Null(disconnected.TotalCost);
        var gateway = await _client.GetFromJsonAsync<MeshRoute>(
            $"/api/topology/route?startNodeId={mesh.GatewayId}");
        Assert.Equal(mesh.GatewayId, Assert.Single(gateway!.NodeIds));
        Assert.Equal(0, gateway.TotalCost);
    }

    [Fact]
    public async Task TopologyLinkEndpointsValidateAndApplySymmetricChanges()
    {
        var mesh = _app.Services.GetRequiredService<IMeshRepository>();
        var sensor = AddSensor(SensorCategory.Environmental);
        var link = new MeshLinkRequest(mesh.GatewayId, sensor, 2.5);
        using var added = await _client.PostAsJsonAsync("/api/topology/links", link);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);
        using var duplicate = await _client.PostAsJsonAsync("/api/topology/links",
            new MeshLinkRequest(sensor, mesh.GatewayId, 9));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var updated = await _client.PutAsJsonAsync("/api/topology/links", link with { Cost = 4 });
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var graph = await _client.GetFromJsonAsync<MeshGraph>("/api/topology");
        Assert.Equal(new MeshConnection(sensor, 4), Assert.Single(graph!.Adjacency[mesh.GatewayId]));
        Assert.Equal(new MeshConnection(mesh.GatewayId, 4), Assert.Single(graph.Adjacency[sensor]));
        foreach (var invalid in new[] { link with { Cost = 0 }, link with { Cost = -1 },
            link with { Cost = MeshLinkRequest.MaximumCost + 1 }, link with { Cost = 1e308 },
            link with { FirstNodeId = sensor }, link with { FirstNodeId = Guid.Empty } })
        {
            using var rejected = await _client.PutAsJsonAsync("/api/topology/links", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var rejectedAdd = await _client.PostAsJsonAsync("/api/topology/links", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedAdd.StatusCode);
        }
        using var unknown = await _client.PostAsJsonAsync("/api/topology/links", link with { SecondNodeId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var route = $"/api/topology/links?firstNodeId={sensor}&secondNodeId={mesh.GatewayId}";
        using var removed = await _client.DeleteAsync(route);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.All(mesh.GetGraph().Adjacency.Values, links => Assert.Empty(links));
        using var missing = await _client.DeleteAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingUpdate = await _client.PutAsJsonAsync("/api/topology/links", link);
        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
        using var invalidDelete = await _client.DeleteAsync("/api/topology/links");
        Assert.Equal(HttpStatusCode.BadRequest, invalidDelete.StatusCode);

        var relay = AddSensor(SensorCategory.Actuator);
        _repository.UpdateHeartbeat(sensor, DateTimeOffset.UtcNow);
        _repository.UpdateHeartbeat(relay, DateTimeOffset.UtcNow);
        foreach (var connection in new[] { link with { Cost = 8 },
            new MeshLinkRequest(mesh.GatewayId, relay, 1), new MeshLinkRequest(relay, sensor, 2) })
        {
            using var response = await _client.PostAsJsonAsync("/api/topology/links", connection);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        var routeUrl = $"/api/topology/route?startNodeId={sensor}";
        var initialRoute = await _client.GetFromJsonAsync<MeshRoute>(routeUrl);
        Assert.Equal(new[] { sensor, relay, mesh.GatewayId }, initialRoute!.NodeIds);
        Assert.Equal(3, initialRoute.TotalCost);

        using var cheaper = await _client.PutAsJsonAsync("/api/topology/links", link with { Cost = 0.5 });
        Assert.Equal(HttpStatusCode.NoContent, cheaper.StatusCode);
        var direct = await _client.GetFromJsonAsync<MeshRoute>(routeUrl);
        Assert.Equal(new[] { sensor, mesh.GatewayId }, direct!.NodeIds);
        Assert.Equal(0.5, direct.TotalCost);
        var tree = await _client.GetFromJsonAsync<MeshSpanningTree>(
            $"/api/topology/spanning-tree?startNodeId={mesh.GatewayId}");
        Assert.Equal(1.5, tree!.TotalCost);
        Assert.Equal(2, tree.Edges.Count);

        using var removeDirect = await _client.DeleteAsync(route);
        Assert.Equal(HttpStatusCode.NoContent, removeDirect.StatusCode);
        var alternate = await _client.GetFromJsonAsync<MeshRoute>(routeUrl);
        Assert.Equal(initialRoute.NodeIds, alternate!.NodeIds);
        Assert.Equal(initialRoute.TotalCost, alternate.TotalCost);

        using var isolate = await _client.DeleteAsync(
            $"/api/topology/links?firstNodeId={relay}&secondNodeId={sensor}");
        Assert.Equal(HttpStatusCode.NoContent, isolate.StatusCode);
        var disconnected = await _client.GetFromJsonAsync<MeshRoute>(routeUrl);
        Assert.False(disconnected!.IsReachable);
        Assert.Empty(disconnected.NodeIds);
        Assert.Null(disconnected.TotalCost);
        var reachable = await _client.GetFromJsonAsync<MeshReachability>(
            $"/api/topology/reachability?startNodeId={mesh.GatewayId}");
        Assert.Equal(sensor, Assert.Single(reachable!.UnreachableNodeIds));
        var finalGraph = await _client.GetFromJsonAsync<MeshGraph>("/api/topology");
        Assert.True(finalGraph!.Nodes[sensor].IsAvailable);
        Assert.Empty(finalGraph.Adjacency[sensor]);
        using var maximum = await _client.PostAsJsonAsync("/api/topology/links",
            link with { Cost = MeshLinkRequest.MaximumCost });
        Assert.Equal(HttpStatusCode.NoContent, maximum.StatusCode);
        var maximumRoute = await _client.GetFromJsonAsync<MeshRoute>(routeUrl);
        Assert.Equal(MeshLinkRequest.MaximumCost, maximumRoute!.TotalCost);
        var maximumTree = await _client.GetFromJsonAsync<MeshSpanningTree>(
            $"/api/topology/spanning-tree?startNodeId={mesh.GatewayId}");
        Assert.Equal(MeshLinkRequest.MaximumCost + 1, maximumTree!.TotalCost);
    }

    [Fact]
    public async Task TopologyEndpointsRejectMissingMalformedAndUnknownNodeIds()
    {
        foreach (var endpoint in new[] { "reachability", "spanning-tree", "route" })
        {
            foreach (var query in new[] { "", "?startNodeId=", "?startNodeId=invalid", $"?startNodeId={Guid.Empty}" })
            {
                using var invalid = await _client.GetAsync($"/api/topology/{endpoint}{query}");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }

            using var unknown = await _client.GetAsync($"/api/topology/{endpoint}?startNodeId={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("The selected mesh node does not exist.", await unknown.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task TypedEndpointsReturnReceiptsAndWorkerProcessesAcceptedPackets()
    {
        var environmental = AddSensor(SensorCategory.Environmental);
        var power = AddSensor(SensorCategory.PowerConsumption);
        var actuator = AddSensor(SensorCategory.Actuator);
        var timestamp = DateTimeOffset.UtcNow;

        using var environmentalResponse = await _client.PostAsJsonAsync(
            "/api/telemetry/environmental", new TelemetryPacket<float>(environmental, 22f, timestamp, "C"));
        using var powerResponse = await _client.PostAsJsonAsync(
            "/api/telemetry/power", new TelemetryPacket<int>(power, 4400, timestamp, "W"));
        using var actuatorResponse = await _client.PostAsJsonAsync(
            "/api/telemetry/actuator", new TelemetryPacket<bool>(actuator, false, timestamp, "state"));

        foreach (var (response, sensorId) in new[]
                 { (environmentalResponse, environmental), (powerResponse, power), (actuatorResponse, actuator) })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<TelemetryAcceptanceResponse>();
            Assert.NotNull(receipt);
            Assert.Equal(sensorId, receipt.SensorId);
            Assert.True(receipt.AcceptedAt >= timestamp);
        }

        var before = await _client.GetFromJsonAsync<TelemetryReadingResponse[]>("/api/telemetry/history");
        Assert.Empty(before!);
        Assert.Equal(3, _queue.Count);

        var worker = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);

        try
        {
            while (worker.GetStatus().Processed < 3)
            {
                await Task.Delay(10, timeout.Token);
            }

            var history = await _client.GetFromJsonAsync<TelemetryReadingResponse[]>("/api/telemetry/history");
            Assert.NotNull(history);
            Assert.Equal(new[] { power, environmental, actuator }, history.Select(reading => reading.SensorId));
            var time = Uri.EscapeDataString(timestamp.ToString("O"));
            foreach (var sensorId in new[] { environmental, power, actuator })
            {
                var range = await _client.GetFromJsonAsync<TelemetryHistoryRangeResponse>(
                    $"/api/telemetry/history/range?sensorId={sensorId}&from={time}&to={time}");
                Assert.Equal(history.Single(reading => reading.SensorId == sensorId), Assert.Single(range!.Readings));
                Assert.False(range.HasMore);
            }
            var status = await _client.GetFromJsonAsync<TelemetryProcessingStatus>("/api/telemetry/processing");
            Assert.Equal(new TelemetryProcessingStatus(0, _queue.Capacity, 3, 0), status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnknownDevicesAndWrongCategoriesNeverReachQueue()
    {
        var environmental = AddSensor(SensorCategory.Environmental);
        var timestamp = DateTimeOffset.UtcNow;
        using var unknown = await _client.PostAsJsonAsync("/api/telemetry/power",
            new TelemetryPacket<int>(Guid.NewGuid(), 1200, timestamp, "W"));
        using var wrongCategory = await _client.PostAsJsonAsync("/api/telemetry/power",
            new TelemetryPacket<int>(environmental, 1200, timestamp, "W"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCategory.StatusCode);
        Assert.Equal(0, _queue.Count);
    }

    [Theory]
    [InlineData("sensorId")]
    [InlineData("value")]
    [InlineData("timestamp")]
    [InlineData("unit")]
    public async Task MissingPacketFieldsAreRejected(string missingField)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sensorId"] = AddSensor(SensorCategory.Actuator),
            ["value"] = false,
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["unit"] = "state"
        };
        payload.Remove(missingField);

        using var response = await _client.PostAsJsonAsync("/api/telemetry/actuator", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _queue.Count);
    }

    [Theory]
    [InlineData("sensorId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("timestamp", "0001-01-01T00:00:00+00:00")]
    [InlineData("unit", " ")]
    [InlineData("unit", null)]
    [InlineData("unit", "abcdefghijklmnopqrstuvwxyz1234567")]
    [InlineData("value", "not-a-number")]
    [InlineData("value", "NaN")]
    public async Task InvalidPacketFieldsAreRejected(string field, string? value)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sensorId"] = AddSensor(SensorCategory.Environmental),
            ["value"] = 22f,
            ["timestamp"] = DateTimeOffset.UtcNow,
            ["unit"] = "C"
        };
        payload[field] = value;

        using var response = await _client.PostAsJsonAsync("/api/telemetry/environmental", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task FullQueueReturnsServiceUnavailableWithoutDroppingAcceptedPackets()
    {
        var sensor = AddSensor(SensorCategory.PowerConsumption);
        var packet = new TelemetryPacket<int>(sensor, 1200, DateTimeOffset.UtcNow, "W");

        for (var index = 0; index < _queue.Capacity; index++)
        {
            using var accepted = await _client.PostAsJsonAsync("/api/telemetry/power", packet);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var response = await _client.PostAsJsonAsync("/api/telemetry/power", packet);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("queue is full", await response.Content.ReadAsStringAsync());
        Assert.Equal(_queue.Capacity, _queue.Count);
    }

    [Fact]
    public async Task LiveSimulationSendsThroughApiAndStopsWithoutFurtherReadings()
    {
        var simulator = _app.Services.GetRequiredService<SimulationWorker>();
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(timeout.Token);
        await simulator.StartAsync(timeout.Token);

        try
        {
            using var started = await _client.PostAsJsonAsync("/api/simulation/start", new { readingsPerSecond = 1 });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            while (processor.GetStatus().Processed < 3)
            {
                await Task.Delay(20, timeout.Token);
            }

            Assert.Equal(3, _repository.GetAll().Count);
            using var stopped = await _client.PostAsync("/api/simulation/stop", null);
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
            Assert.False((await stopped.Content.ReadFromJsonAsync<SimulationStatus>())!.IsRunning);
            var processed = processor.GetStatus().Processed;
            await Task.Delay(1200, timeout.Token);
            Assert.Equal(processed, processor.GetStatus().Processed);
            Assert.Equal(0, processor.GetStatus().Failed);
        }
        finally
        {
            await simulator.StopAsync(CancellationToken.None);
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task SimulationRejectsInvalidRates(int readingsPerSecond)
    {
        using var response = await _client.PostAsJsonAsync("/api/simulation/start", new { readingsPerSecond });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(_app.Services.GetRequiredService<SimulationWorker>().GetStatus().IsRunning);
    }

    [Fact]
    public async Task PowerSpikeScenarioRepeatedlyBypassesTheNormalBacklog()
    {
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(timeout.Token);

        try
        {
            for (var run = 0; run < 2; run++)
            {
                using var response = await _client.PostAsJsonAsync("/api/simulation/scenario",
                    new { scenario = SimulationScenario.PowerSpike });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = (await response.Content.ReadFromJsonAsync<SimulationScenarioResult>())!;
                Assert.Equal(21, result.PacketIds.Count);
                Assert.NotEqual(result.MeterId, result.RelayId);

                while (processor.GetRecentProcessing().Count(item => result.PacketIds.Contains(item.PacketId)) < 21)
                {
                    await Task.Delay(10, timeout.Token);
                }

                var history = (await _client.GetFromJsonAsync<TelemetryProcessingEvent[]>("/api/telemetry/processing/history"))!;
                var events = history.Where(item => result.PacketIds.Contains(item.PacketId)).ToArray();
                Assert.Equal(result.PacketIds[^1], events[0].PacketId);
                Assert.Equal(result.MeterId, events[0].SensorId);
                Assert.True(events[0].Critical);
                Assert.True(events[0].ArrivalOrder > events[1].ArrivalOrder);
                Assert.Equal(result.PacketIds.Take(20), events.Skip(1).Select(item => item.PacketId));
                Assert.All(events, item =>
                {
                    Assert.True(item.Succeeded);
                    Assert.True(item.QueueLatencyMilliseconds >= 0);
                    Assert.True(item.ProcessedAt >= item.EnqueuedAt);
                });
            }
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedScenarioAlwaysResumesQueueProcessing()
    {
        var sensor = AddSensor(SensorCategory.Environmental);
        for (var index = 0; index < 20; index++)
        {
            _queue.TryEnqueue(new TelemetryWorkItem.Environmental(
                new(sensor, 22f, DateTimeOffset.UtcNow, "C")));
        }

        using var response = await _client.PostAsJsonAsync("/api/simulation/scenario",
            new { scenario = SimulationScenario.PowerSpike });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(_queue.Capacity, _queue.Count);
        Assert.True(_queue.TryDequeue(out _));
        var status = _app.Services.GetRequiredService<SimulationWorker>().GetStatus();
        Assert.False(status.IsRunning);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task DisconnectScenarioPausesOnlyRelayAndReconnectClearsItsAlert()
    {
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        var simulator = _app.Services.GetRequiredService<SimulationWorker>();
        var dashboard = _app.Services.GetRequiredService<DashboardService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await processor.StartAsync(timeout.Token);
        await simulator.StartAsync(timeout.Token);

        try
        {
            using var response = await _client.PostAsJsonAsync("/api/simulation/scenario",
                new { scenario = SimulationScenario.Disconnect });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<SimulationScenarioResult>())!;
            while (_repository.GetById(result.RelayId)!.LastHeartbeat is null)
            {
                await Task.Delay(10, timeout.Token);
            }

            var heartbeat = _repository.GetById(result.RelayId)!.LastHeartbeat;
            await Task.Delay(TimeSpan.FromSeconds(31), timeout.Token);

            Assert.Equal(heartbeat, _repository.GetById(result.RelayId)!.LastHeartbeat);
            Assert.True(_repository.GetById(result.MeterId)!.LastHeartbeat > heartbeat);
            var disconnected = dashboard.GetDashboard();
            Assert.Equal(1, disconnected.DisconnectedSensors);
            var alert = Assert.Single(disconnected.ActiveIssues, issue => issue.IssueType == "Disconnect");
            Assert.Equal(result.RelayId, alert.SensorId);

            using var reconnect = await _client.PostAsJsonAsync("/api/simulation/scenario",
                new { scenario = SimulationScenario.Reconnect });
            Assert.Equal(HttpStatusCode.OK, reconnect.StatusCode);
            while (_repository.GetById(result.RelayId)!.LastHeartbeat == heartbeat)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.DoesNotContain(dashboard.GetDashboard().ActiveIssues,
                issue => issue.SensorId == result.RelayId && issue.IssueType == "Disconnect");
            Assert.False(simulator.GetStatus().RelayDisconnected);
        }
        finally
        {
            await simulator.StopAsync(CancellationToken.None);
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task IndividualDemoPauseExpiresHeartbeatReroutesAndResumesThroughHttp()
    {
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        var simulator = _app.Services.GetRequiredService<SimulationWorker>();
        var topology = _app.Services.GetRequiredService<MeshTopologyService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await processor.StartAsync(timeout.Token);
        await simulator.StartAsync(timeout.Token);
        try
        {
            using var seed = await _client.PostAsync("/api/simulation/seed?packetsPerSensor=10", null, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
            using var normal = await _client.PostAsJsonAsync("/api/simulation/scenario",
                new { scenario = SimulationScenario.Normal }, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
            var result = (await normal.Content.ReadFromJsonAsync<SimulationScenarioResult>(timeout.Token))!;
            var climate = _repository.GetAll().Single(sensor => sensor.DeviceIdentifier == "ESP32-ENV-01").Id;
            while (topology.GetGatewayRoute(result.MeterId).NodeIds.Count != 4)
            {
                await Task.Delay(10, timeout.Token);
            }

            for (var index = 0; index < 2; index++)
            {
                using var pause = await _client.PostAsync($"/api/simulation/devices/{climate}/pause", null, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
                var status = (await pause.Content.ReadFromJsonAsync<SimulationStatus>(timeout.Token))!;
                Assert.True(status.IsRunning);
                Assert.Equal(climate, Assert.Single(status.PausedSensorIds));
            }

            await Task.Delay(1000, timeout.Token);
            var heartbeat = _repository.GetById(climate)!.LastHeartbeat;
            await Task.Delay(TimeSpan.FromSeconds(31), timeout.Token);
            Assert.Equal(heartbeat, _repository.GetById(climate)!.LastHeartbeat);
            Assert.True(_repository.GetById(result.MeterId)!.LastHeartbeat > heartbeat);
            Assert.True(_repository.GetById(result.RelayId)!.LastHeartbeat > heartbeat);
            Assert.False(topology.GetGraph().Nodes[climate].IsAvailable);
            var rerouted = topology.GetGatewayRoute(result.MeterId);
            Assert.Equal(2, rerouted.NodeIds.Count);
            Assert.Equal(8, rerouted.TotalCost);

            using var resume = await _client.PostAsync($"/api/simulation/devices/{climate}/resume", null, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
            Assert.Empty((await resume.Content.ReadFromJsonAsync<SimulationStatus>(timeout.Token))!.PausedSensorIds);
            while (topology.GetGatewayRoute(result.MeterId).NodeIds.Count != 4)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.True(_repository.GetById(climate)!.LastHeartbeat > heartbeat);

            using var relayPause = await _client.PostAsync($"/api/simulation/devices/{result.RelayId}/pause", null, timeout.Token);
            Assert.True((await relayPause.Content.ReadFromJsonAsync<SimulationStatus>(timeout.Token))!.RelayDisconnected);
            using var relayResume = await _client.PostAsync($"/api/simulation/devices/{result.RelayId}/resume", null, timeout.Token);
            Assert.False((await relayResume.Content.ReadFromJsonAsync<SimulationStatus>(timeout.Token))!.RelayDisconnected);
        }
        finally
        {
            await simulator.StopAsync(CancellationToken.None);
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task IndividualPauseRejectsUnknownAndNonDemoSensors()
    {
        var custom = AddSensor(SensorCategory.Environmental);
        foreach (var action in new[] { "pause", "resume" })
        {
            foreach (var (id, expected) in new[]
            {
                (Guid.Empty, HttpStatusCode.BadRequest),
                (Guid.NewGuid(), HttpStatusCode.NotFound),
                (custom, HttpStatusCode.BadRequest)
            })
            {
                using var response = await _client.PostAsync($"/api/simulation/devices/{id}/{action}", null);
                Assert.Equal(expected, response.StatusCode);
            }
        }
        Assert.Empty(_app.Services.GetRequiredService<SimulationWorker>().GetStatus().PausedSensorIds);
    }

    [Fact]
    public async Task UnknownScenarioIsRejectedWithoutQueuingPackets()
    {
        using var response = await _client.PostAsJsonAsync("/api/simulation/scenario", new { scenario = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task LiveSimulationPreservesManualRelayState()
    {
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        var demo = _app.Services.GetRequiredService<DemoDataService>();
        var commands = _app.Services.GetRequiredService<DeviceCommandService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(timeout.Token);

        try
        {
            await demo.SendLiveAsync(1, timeout.Token);
            var relay = _repository.GetAll().Single(sensor => sensor.Category == SensorCategory.Actuator);
            while (commands.GetState(relay.Id) is null || relay.LastHeartbeat is null)
            {
                await Task.Delay(10, timeout.Token);
            }

            var command = commands.Execute(relay.Id, false);
            Assert.Equal(DeviceCommandOutcome.Succeeded, command.Outcome);
            await demo.SendLiveAsync(3, timeout.Token);
            while (processor.GetStatus().Processed < 12)
            {
                await Task.Delay(10, timeout.Token);
            }

            var profile = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{relay.Id}");
            Assert.False(profile!.ActuatorState!.IsOn);
            Assert.Equal(command.StateVersion, profile.ActuatorState.Version);
            var telemetry = _app.Services.GetRequiredService<TelemetryService>();
            Assert.All(telemetry.GetHistory(relay.Id, 3), reading => Assert.Equal("Off", reading.Value));
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CommandAndUndoRetriesChangeStateAndHistoryOnlyOnce()
    {
        var sensorId = AddSensor(SensorCategory.Actuator);
        _app.Services.GetRequiredService<TelemetryService>().IngestActuator(
            new TelemetryPacket<bool>(sensorId, true, DateTimeOffset.UtcNow, "state"));
        var request = new DeviceCommandRequest(Guid.NewGuid(), sensorId, false);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => _client.PostAsJsonAsync("/api/commands", request)));
        var results = new List<DeviceCommandResult>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                results.Add((await response.Content.ReadFromJsonAsync<DeviceCommandResult>())!);
            }
        }

        Assert.All(results, result => Assert.Equal(results[0], result));
        var profile = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensorId}");
        Assert.False(profile!.ActuatorState!.IsOn);
        using var noChange = await _client.PostAsJsonAsync("/api/commands",
            request with { RequestId = Guid.NewGuid() });
        Assert.Equal(DeviceCommandOutcome.NoChange,
            (await noChange.Content.ReadFromJsonAsync<DeviceCommandResult>())!.Outcome);

        var undoRequest = new UndoCommandRequest(Guid.NewGuid());
        using var undo = await _client.PostAsJsonAsync("/api/commands/undo", undoRequest);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        var restored = (await undo.Content.ReadFromJsonAsync<DeviceCommandResult>())!;
        Assert.Equal(results[0].Id, restored.UndoesCommandId);
        using var repeatedUndo = await _client.PostAsJsonAsync("/api/commands/undo", undoRequest);
        Assert.Equal(restored, await repeatedUndo.Content.ReadFromJsonAsync<DeviceCommandResult>());
        using var repeatedCommand = await _client.PostAsJsonAsync("/api/commands", request);
        Assert.Equal(results[0], await repeatedCommand.Content.ReadFromJsonAsync<DeviceCommandResult>());
        profile = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensorId}");
        Assert.True(profile!.ActuatorState!.IsOn);
        var history = await _client.GetFromJsonAsync<DeviceCommandResult[]>("/api/commands/history");
        Assert.Equal(3, history!.Length);
        Assert.Equal(restored.Id, history[0].Id);
        Assert.Single((await _client.GetFromJsonAsync<DeviceCommandResult[]>("/api/commands/history?limit=1"))!);

        using var changedRequest = await _client.PostAsJsonAsync("/api/commands", request with { IsOn = true });
        Assert.Equal(HttpStatusCode.Conflict, changedRequest.StatusCode);
        using var reusedRequest = await _client.PostAsJsonAsync("/api/commands/undo",
            new UndoCommandRequest(request.RequestId));
        Assert.Equal(HttpStatusCode.Conflict, reusedRequest.StatusCode);
    }

    [Theory]
    [InlineData("/api/commands", "{}")]
    [InlineData("/api/commands", "{\"requestId\":\"invalid\"}")]
    [InlineData("/api/commands", "{\"requestId\":\"11111111-1111-1111-1111-111111111111\",\"sensorId\":\"22222222-2222-2222-2222-222222222222\"}")]
    [InlineData("/api/commands/undo", "{}")]
    [InlineData("/api/commands/undo", "null")]
    public async Task InvalidCommandPayloadIsRejected(string path, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(path, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_app.Services.GetRequiredService<DeviceCommandService>().GetHistory());
    }

    [Fact]
    public async Task CommandEndpointsReportMissingWrongCategoryAndDisconnectedDevices()
    {
        using var missing = await _client.PostAsJsonAsync("/api/commands",
            new DeviceCommandRequest(Guid.NewGuid(), Guid.NewGuid(), false));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var environment = AddSensor(SensorCategory.Environmental);
        using var wrongCategory = await _client.PostAsJsonAsync("/api/commands",
            new DeviceCommandRequest(Guid.NewGuid(), environment, false));
        Assert.Equal(HttpStatusCode.BadRequest, wrongCategory.StatusCode);
        var sensorId = AddSensor(SensorCategory.Actuator);
        var request = new DeviceCommandRequest(Guid.NewGuid(), sensorId, false);
        using var disconnected = await _client.PostAsJsonAsync("/api/commands", request);
        Assert.Equal(HttpStatusCode.Conflict, disconnected.StatusCode);
        var rejected = await disconnected.Content.ReadFromJsonAsync<DeviceCommandResult>();
        _app.Services.GetRequiredService<TelemetryService>().IngestActuator(
            new TelemetryPacket<bool>(sensorId, true, DateTimeOffset.UtcNow, "state"));
        using var retry = await _client.PostAsJsonAsync("/api/commands", request);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(rejected, await retry.Content.ReadFromJsonAsync<DeviceCommandResult>());
        using var fresh = await _client.PostAsJsonAsync("/api/commands", request with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var invalidLimit = await _client.GetAsync("/api/commands/history?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
    }

    [Fact]
    public async Task RetryingEmptyUndoDoesNotUndoALaterCommand()
    {
        var request = new UndoCommandRequest(Guid.NewGuid());
        using var empty = await _client.PostAsJsonAsync("/api/commands/undo", request);
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        var sensorId = AddSensor(SensorCategory.Actuator);
        _app.Services.GetRequiredService<TelemetryService>().IngestActuator(
            new TelemetryPacket<bool>(sensorId, true, DateTimeOffset.UtcNow, "state"));
        using var command = await _client.PostAsJsonAsync("/api/commands",
            new DeviceCommandRequest(Guid.NewGuid(), sensorId, false));
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        using var retry = await _client.PostAsJsonAsync("/api/commands/undo", request);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var profile = await _client.GetFromJsonAsync<SensorResponse>($"/api/sensors/{sensorId}");
        Assert.False(profile!.ActuatorState!.IsOn);
        Assert.Single(_app.Services.GetRequiredService<DeviceCommandService>().GetHistory());
    }

    [Fact]
    public async Task CommandStreamCombinesFiltersBeforeLimitingReadings()
    {
        var power = AddSensor(SensorCategory.PowerConsumption);
        var environment = AddSensor(SensorCategory.Environmental);
        AddSensor(SensorCategory.Actuator);
        var telemetry = _app.Services.GetRequiredService<TelemetryService>();
        var now = DateTimeOffset.UtcNow;
        telemetry.IngestPower(new TelemetryPacket<int>(power, 4400, now.AddSeconds(-2), "W"));
        telemetry.IngestEnvironmentalBatch(Enumerable.Range(0, 1005)
            .Select(index => new TelemetryPacket<float>(environment, 22, now.AddMilliseconds(index), "C"))
            .ToArray());

        var stream = await _client.GetFromJsonAsync<CommandStreamResponse>(
            "/api/command-stream?search=%20POWER%20&category=PowerConsumption&alert=Telemetry&limit=1");
        Assert.Equal(power, Assert.Single(stream!.Devices).Id);
        Assert.Equal(power, Assert.Single(stream.Readings).SensorId);
        Assert.Equal(power, Assert.Single(stream.ActiveIssues).SensorId);

        var location = await _client.GetFromJsonAsync<CommandStreamResponse>(
            "/api/command-stream?search=facility%20a&category=Environmental&limit=3");
        Assert.Equal(environment, Assert.Single(location!.Devices).Id);
        Assert.Equal(3, location.Readings.Count);
        Assert.Equal(location.Readings.OrderBy(reading => reading.Timestamp), location.Readings);
        Assert.Empty(location.ActiveIssues);

        var empty = await _client.GetFromJsonAsync<CommandStreamResponse>(
            "/api/command-stream?search=missing-device");
        Assert.Empty(empty!.Devices);
        Assert.Empty(empty.Readings);
        Assert.Empty(empty.ActiveIssues);
    }

    [Fact]
    public async Task CommandStreamRefreshesDisconnectAlertsAndReturnsCurrentActuatorState()
    {
        var sensorId = AddSensor(SensorCategory.Actuator);
        var disconnected = await _client.GetFromJsonAsync<CommandStreamResponse>(
            "/api/command-stream?alert=Disconnected");
        Assert.Equal(SensorStatus.Disconnected, Assert.Single(disconnected!.Devices).Status);
        Assert.Equal("Disconnect", Assert.Single(disconnected.ActiveIssues).IssueType);
        Assert.Null(disconnected.Devices[0].ActuatorState);
        var active = await _client.GetFromJsonAsync<CommandStreamResponse>("/api/command-stream?alert=Active");
        Assert.Equal(sensorId, Assert.Single(active!.Devices).Id);

        _app.Services.GetRequiredService<TelemetryService>().IngestActuator(
            new TelemetryPacket<bool>(sensorId, true, DateTimeOffset.UtcNow, "state"));
        _app.Services.GetRequiredService<DeviceCommandService>().Execute(sensorId, false);
        var connected = await _client.GetFromJsonAsync<CommandStreamResponse>("/api/command-stream");
        var device = Assert.Single(connected!.Devices);
        Assert.Equal(SensorStatus.Connected, device.Status);
        Assert.False(device.ActuatorState!.IsOn);
        Assert.Empty(connected.ActiveIssues);
        var cleared = await _client.GetFromJsonAsync<CommandStreamResponse>("/api/command-stream?alert=Active");
        Assert.Empty(cleared!.Devices);
        Assert.Empty(cleared.Readings);

        _repository.GetById(sensorId)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        var overdue = await _client.GetFromJsonAsync<CommandStreamResponse>(
            "/api/command-stream?alert=Disconnected");
        Assert.Equal(sensorId, Assert.Single(overdue!.Devices).Id);
        Assert.Single(overdue.ActiveIssues);
    }

    [Theory]
    [InlineData("category=99")]
    [InlineData("category=invalid")]
    [InlineData("alert=99")]
    [InlineData("alert=invalid")]
    [InlineData("limit=0")]
    [InlineData("limit=1001")]
    public async Task CommandStreamRejectsInvalidFilters(string query)
    {
        using var response = await _client.GetAsync($"/api/command-stream?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OnlyDeliberateSearchesRecordUnfilteredConditionContext()
    {
        var power = AddSensor(SensorCategory.PowerConsumption);
        var relay = AddSensor(SensorCategory.Actuator);
        var telemetry = _app.Services.GetRequiredService<TelemetryService>();
        var activity = _app.Services.GetRequiredService<UserActivityService>();
        telemetry.IngestPower(new TelemetryPacket<int>(power, 4400, DateTimeOffset.UtcNow, "W"));
        using var refresh = await _client.GetAsync("/api/command-stream?search=Actuator");
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Empty(activity.GetHistory());

        using var search = await _client.PostAsJsonAsync("/api/command-stream/search",
            new CommandStreamQuery { Search = "  Actuator  ", Category = SensorCategory.Actuator });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var recorded = Assert.Single(activity.GetHistory());
        Assert.Equal(UserActivityKind.Search, recorded.Kind);
        Assert.Equal("Actuator", recorded.Search);
        Assert.Equal(SensorCategory.Actuator, recorded.Category);
        Assert.Equal(relay, Assert.Single(recorded.MatchedSensorIds));
        Assert.Contains(recorded.Conditions, condition => condition.SensorId == power && condition.IssueType == "Telemetry");
        Assert.Contains(recorded.Conditions, condition => condition.SensorId == relay && condition.IssueType == "Disconnect");

        using var cleared = await _client.PostAsJsonAsync("/api/command-stream/search",
            new CommandStreamQuery { Search = "  " });
        using var invalid = await _client.PostAsJsonAsync("/api/command-stream/search",
            new CommandStreamQuery { Category = (SensorCategory)99 });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Single(activity.GetHistory());
        using var filter = await _client.PostAsJsonAsync("/api/command-stream/search",
            new CommandStreamQuery { Alert = CommandStreamAlertFilter.Active });
        Assert.Equal(HttpStatusCode.OK, filter.StatusCode);
        Assert.Equal(2, activity.GetHistory().Count);
    }

    [Fact]
    public async Task CommandActivityPreservesContextOutcomesAndUndoWithoutRecordingRetries()
    {
        var power = AddSensor(SensorCategory.PowerConsumption);
        var relay = AddSensor(SensorCategory.Actuator);
        var telemetry = _app.Services.GetRequiredService<TelemetryService>();
        var activity = _app.Services.GetRequiredService<UserActivityService>();
        telemetry.IngestPower(new TelemetryPacket<int>(power, 4400, DateTimeOffset.UtcNow, "W"));
        telemetry.IngestActuator(new TelemetryPacket<bool>(relay, true, DateTimeOffset.UtcNow, "state"));
        var request = new DeviceCommandRequest(Guid.NewGuid(), relay, false);
        using var command = await _client.PostAsJsonAsync("/api/commands", request);
        Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        var recorded = Assert.Single(activity.GetHistory());
        Assert.Equal(UserActivityKind.Command, recorded.Kind);
        Assert.Equal(DeviceCommandOutcome.Succeeded, recorded.Command!.Outcome);
        Assert.True(recorded.Command.PreviousState);
        Assert.False(recorded.Command.RequestedState);
        Assert.Equal(power, Assert.Single(recorded.Conditions).SensorId);

        _app.Services.GetRequiredService<ActiveIssueService>().Resolve(power, "Telemetry");
        using var retry = await _client.PostAsJsonAsync("/api/commands", request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(activity.GetHistory());
        Assert.Single(recorded.Conditions);

        using var noChange = await _client.PostAsJsonAsync("/api/commands", request with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, noChange.StatusCode);
        Assert.Equal(DeviceCommandOutcome.NoChange, activity.GetHistory()[1].Command!.Outcome);
        Assert.Empty(activity.GetHistory()[1].Conditions);
        var undoRequest = new UndoCommandRequest(Guid.NewGuid());
        using var undo = await _client.PostAsJsonAsync("/api/commands/undo", undoRequest);
        using var undoRetry = await _client.PostAsJsonAsync("/api/commands/undo", undoRequest);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, undoRetry.StatusCode);
        Assert.Equal(3, activity.GetHistory().Count);
        Assert.Equal(UserActivityKind.Undo, activity.GetHistory()[2].Kind);
        Assert.Equal(recorded.Command.Id, activity.GetHistory()[2].Command!.UndoesCommandId);

        _repository.GetById(relay)!.LastHeartbeat = DateTimeOffset.UtcNow.AddSeconds(-31);
        using var rejected = await _client.PostAsJsonAsync("/api/commands", request with { RequestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var last = activity.GetHistory()[3];
        Assert.Equal(DeviceCommandOutcome.Rejected, last.Command!.Outcome);
        Assert.Contains(last.Conditions, condition => condition.SensorId == relay && condition.IssueType == "Disconnect");
    }

    [Fact]
    public async Task SuggestionsEndpointUsesLearnedSearchesWithoutExecutingCommands()
    {
        var power = AddSensor(SensorCategory.PowerConsumption);
        var relay = AddSensor(SensorCategory.Actuator);
        var telemetry = _app.Services.GetRequiredService<TelemetryService>();
        telemetry.IngestPower(new TelemetryPacket<int>(power, 4400, DateTimeOffset.UtcNow, "W"));
        telemetry.IngestActuator(new TelemetryPacket<bool>(relay, false, DateTimeOffset.UtcNow, "state"));
        Assert.Empty((await _client.GetFromJsonAsync<ActionSuggestion[]>("/api/suggestions"))!);
        for (var index = 0; index < 2; index++)
        {
            using var search = await _client.PostAsJsonAsync("/api/command-stream/search",
                new CommandStreamQuery { Search = "Actuator" });
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        }

        var suggestions = await _client.GetFromJsonAsync<ActionSuggestion[]>("/api/suggestions?limit=1");
        var suggestion = Assert.Single(suggestions!);
        Assert.Equal(relay, suggestion.TargetSensorId);
        Assert.Equal(LearnedAction.InspectDevice, suggestion.Action);
        Assert.Equal(2, suggestion.EvidenceCount);
        Assert.False(_app.Services.GetRequiredService<DeviceCommandService>().GetState(relay)!.IsOn);
        Assert.Equal(2, _app.Services.GetRequiredService<UserActivityService>().GetHistory().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task SuggestionsEndpointRejectsInvalidLimits(int limit)
    {
        using var response = await _client.GetAsync($"/api/suggestions?limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentHttpBurstRejectsOverflowAndRecoversWithoutLosingAcceptedPackets()
    {
        var sensor = AddSensor(SensorCategory.Environmental);
        var timestamp = DateTimeOffset.UtcNow;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        async Task<int> SendBurst(int offset)
        {
            var accepted = await Task.WhenAll(Enumerable.Range(offset, 64).Select(async index =>
            {
                using var response = await _client.PostAsJsonAsync("/api/telemetry/environmental",
                    new TelemetryPacket<float>(sensor, 22f, timestamp.AddTicks(index), "C"), timeout.Token);
                Assert.True(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable);
                return response.StatusCode == HttpStatusCode.Accepted ? 1 : 0;
            }));
            return accepted.Sum();
        }

        var acceptedCount = await SendBurst(0);
        Assert.Equal(_queue.Capacity, acceptedCount);
        Assert.Equal(_queue.Capacity, _queue.Count);
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        await processor.StartAsync(timeout.Token);

        try
        {
            while (processor.GetStatus().Processed < acceptedCount)
            {
                await Task.Delay(10, timeout.Token);
            }

            for (var burst = 1; burst <= 10; burst++)
            {
                acceptedCount += await SendBurst(burst * 64);
            }

            while (processor.GetStatus().Processed < acceptedCount)
            {
                await Task.Delay(10, timeout.Token);
            }

            Assert.True(acceptedCount > _queue.Capacity);
            var status = await _client.GetFromJsonAsync<TelemetryProcessingStatus>(
                "/api/telemetry/processing", timeout.Token);
            Assert.Equal(new TelemetryProcessingStatus(0, _queue.Capacity, acceptedCount, 0), status);
            var history = (await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
                "/api/telemetry/history?limit=1000", timeout.Token))!;
            Assert.Equal(acceptedCount, history.Length);
            Assert.Equal(acceptedCount, history.Select(reading => reading.Id).Distinct().Count());
            Assert.Equal(history.OrderBy(reading => reading.Timestamp), history);
            Assert.Equal(history[^1].Timestamp, _repository.GetById(sensor)!.LastHeartbeat);
            Assert.Empty(_app.Services.GetRequiredService<ActiveIssueService>().GetActiveIssues());
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HttpWorkflowConnectsTelemetrySearchSuggestionsCommandsAndUndo()
    {
        var power = AddSensor(SensorCategory.PowerConsumption);
        var relay = AddSensor(SensorCategory.Actuator);
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(timeout.Token);

        try
        {
            using var powerResponse = await _client.PostAsJsonAsync("/api/telemetry/power",
                new TelemetryPacket<int>(power, 4400, DateTimeOffset.UtcNow, "W"), timeout.Token);
            using var relayResponse = await _client.PostAsJsonAsync("/api/telemetry/actuator",
                new TelemetryPacket<bool>(relay, true, DateTimeOffset.UtcNow, "state"), timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, powerResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, relayResponse.StatusCode);
            while (processor.GetStatus().Processed < 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            for (var index = 0; index < 2; index++)
            {
                using var search = await _client.PostAsJsonAsync("/api/command-stream/search",
                    new CommandStreamQuery { Search = "Actuator" }, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            }

            var suggestion = Assert.Single((await _client.GetFromJsonAsync<ActionSuggestion[]>(
                "/api/suggestions", timeout.Token))!);
            Assert.Equal(relay, suggestion.TargetSensorId);
            Assert.Equal(LearnedAction.InspectDevice, suggestion.Action);
            Assert.Equal(2, suggestion.EvidenceCount);

            using var command = await _client.PostAsJsonAsync("/api/commands",
                new DeviceCommandRequest(Guid.NewGuid(), relay, false), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, command.StatusCode);
            var stream = await _client.GetFromJsonAsync<CommandStreamResponse>(
                "/api/command-stream?search=Actuator", timeout.Token);
            Assert.False(Assert.Single(stream!.Devices).ActuatorState!.IsOn);

            using var undo = await _client.PostAsJsonAsync("/api/commands/undo",
                new UndoCommandRequest(Guid.NewGuid()), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
            var restored = await _client.GetFromJsonAsync<CommandStreamResponse>(
                "/api/command-stream?search=Actuator", timeout.Token);
            Assert.True(Assert.Single(restored!.Devices).ActuatorState!.IsOn);
            var history = (await _client.GetFromJsonAsync<DeviceCommandResult[]>(
                "/api/commands/history", timeout.Token))!;
            Assert.Equal(2, history.Length);
            Assert.All(history, item => Assert.Equal(DeviceCommandOutcome.Succeeded, item.Outcome));
            Assert.Single(history, item => item.UndoesCommandId is not null);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NormalScenarioClearsPowerSpikeWithoutWaitingForLiveTraffic()
    {
        var processor = _app.Services.GetRequiredService<TelemetryProcessingWorker>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StartAsync(timeout.Token);

        try
        {
            foreach (var scenario in new[] { SimulationScenario.PowerSpike, SimulationScenario.Normal })
            {
                using var response = await _client.PostAsJsonAsync("/api/simulation/scenario",
                    new { scenario }, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = (await response.Content.ReadFromJsonAsync<SimulationScenarioResult>(timeout.Token))!;
                while (processor.GetRecentProcessing().Count(item => result.PacketIds.Contains(item.PacketId)) < result.PacketIds.Count)
                {
                    await Task.Delay(10, timeout.Token);
                }

                var issues = _app.Services.GetRequiredService<ActiveIssueService>().GetActiveIssues();
                if (scenario == SimulationScenario.PowerSpike)
                {
                    Assert.Contains(issues, issue => issue.SensorId == result.MeterId && issue.IssueType == "Telemetry");
                }
                else
                {
                    Assert.DoesNotContain(issues, issue => issue.SensorId == result.MeterId && issue.IssueType == "Telemetry");
                }
            }
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task RegistrationRejectsUndefinedCategories(int category)
    {
        using var response = await _client.PostAsJsonAsync("/api/sensors", new
        {
            DeviceIdentifier = "INVALID-CATEGORY",
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1",
            Category = category
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Select a valid sensor category.", await response.Content.ReadAsStringAsync());
        Assert.Empty(_repository.GetAll());
    }

    [Fact]
    public async Task DashboardInstanceReachesTheFrontendAcrossDataChanges()
    {
        var first = await _client.GetFromJsonAsync<SmartX.Web.Models.DashboardViewModel>("/api/dashboard");
        AddSensor(SensorCategory.Environmental);
        var updated = await _client.GetFromJsonAsync<SmartX.Web.Models.DashboardViewModel>("/api/dashboard");

        Assert.NotNull(first);
        Assert.NotNull(updated);
        Assert.NotEqual(Guid.Empty, first.InstanceId);
        Assert.Equal(first.InstanceId, updated.InstanceId);
        Assert.Equal(0, first.TotalSensors);
        Assert.Equal(1, updated.TotalSensors);
    }

    [Fact]
    public async Task HistoryRangeValidatesInputsAndReturnsLatestMatchingReadings()
    {
        var sensor = AddSensor(SensorCategory.Environmental);
        var otherSensor = AddSensor(SensorCategory.PowerConsumption);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(500);
        var from = Uri.EscapeDataString(start.ToString("O"));
        var to = Uri.EscapeDataString(end.ToOffset(TimeSpan.FromHours(2)).ToString("O"));
        var url = $"/api/telemetry/history/range?sensorId={sensor}&from={from}&to={to}";

        var empty = await _client.GetFromJsonAsync<TelemetryHistoryRangeResponse>(url);
        Assert.Empty(empty!.Readings);
        Assert.False(empty.HasMore);

        foreach (var query in new[]
        {
            "", $"sensorId={sensor}&from={from}", $"sensorId={sensor}&to={to}",
            $"from={from}&to={to}", $"sensorId={Guid.Empty}&from={from}&to={to}",
            $"sensorId=invalid&from={from}&to={to}", $"sensorId={sensor}&from=invalid&to={to}",
            $"sensorId={sensor}&from={from}&to=invalid", $"sensorId={sensor}&from={to}&to={from}",
            $"sensorId={sensor}&from={from}&to={to}&limit=0",
            $"sensorId={sensor}&from={from}&to={to}&limit=-1",
            $"sensorId={sensor}&from={from}&to={to}&limit=1001",
            $"sensorId={sensor}&from={from}&to={to}&limit=invalid"
        })
        {
            using var invalid = await _client.GetAsync($"/api/telemetry/history/range?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var missing = await _client.GetAsync(url.Replace(sensor.ToString(), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var service = _app.Services.GetRequiredService<TelemetryService>();
        var readings = service.IngestEnvironmentalBatch(Enumerable.Range(0, 1001)
            .Select(index => new TelemetryPacket<float>(sensor, 22f, start.AddSeconds((1000 - index) / 2), "C"))
            .ToArray());
        service.IngestPower(new(otherSensor, 1000, end, "W"));
        var ordered = readings.OrderBy(reading => reading.Timestamp).ToArray();

        foreach (var limit in new[] { 1, 200, 1000 })
        {
            var range = await _client.GetFromJsonAsync<TelemetryHistoryRangeResponse>(
                limit == 200 ? url : $"{url}&limit={limit}");
            Assert.Equal(ordered.TakeLast(limit), range!.Readings);
            Assert.True(range.HasMore);
        }

        var boundary = Uri.EscapeDataString(start.AddSeconds(100).ToString("O"));
        var exact = await _client.GetFromJsonAsync<TelemetryHistoryRangeResponse>(
            $"/api/telemetry/history/range?sensorId={sensor}&from={boundary}&to={boundary}&limit=2");
        Assert.Equal(ordered.Where(reading => reading.Timestamp == start.AddSeconds(100)), exact!.Readings);
        Assert.False(exact.HasMore);

        var later = Uri.EscapeDataString(end.AddSeconds(1).ToString("O"));
        var outside = await _client.GetFromJsonAsync<TelemetryHistoryRangeResponse>(
            $"/api/telemetry/history/range?sensorId={sensor}&from={later}&to={later}");
        Assert.Empty(outside!.Readings);
        Assert.False(outside.HasMore);
        var original = await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
            $"/api/telemetry/history?sensorId={sensor}&limit=1000");
        Assert.Equal(ordered.TakeLast(1000), original!);
    }

    private Guid AddSensor(SensorCategory category)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = category.ToString(),
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1",
            Category = category
        };
        _repository.Add(sensor);
        return sensor.Id;
    }
}
