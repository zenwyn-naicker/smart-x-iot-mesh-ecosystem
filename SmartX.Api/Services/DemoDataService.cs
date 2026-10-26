using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class DemoDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly IHttpClientFactory _clientFactory;
    private readonly IMeshRepository _meshRepository;

    public DemoDataService(IHttpClientFactory clientFactory, IMeshRepository meshRepository)
    {
        _clientFactory = clientFactory;
        _meshRepository = meshRepository;
    }

    public Task<SimulationSeedResult> SeedAsync(int packetsPerSensor, CancellationToken cancellationToken)
    {
        return SendAsync(packetsPerSensor, true, cancellationToken);
    }

    public Task<SimulationSeedResult> SendLiveAsync(
        int packetsPerSensor, CancellationToken cancellationToken, bool relayDisconnected = false,
        IReadOnlyCollection<Guid>? pausedSensorIds = null)
    {
        return SendAsync(packetsPerSensor, false, cancellationToken, relayDisconnected, pausedSensorIds);
    }

    private async Task<SimulationSeedResult> SendAsync(
        int packetsPerSensor,
        bool historical,
        CancellationToken cancellationToken,
        bool relayDisconnected = false,
        IReadOnlyCollection<Guid>? pausedSensorIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(packetsPerSensor, 1);
        using var client = _clientFactory.CreateClient("SimulatedDevices");
        var sensors = await EnsureDemoSensorsAsync(client, cancellationToken);
        if (historical)
        {
            EnsureDemoMesh(sensors);
        }

        if (relayDisconnected)
        {
            sensors.RemoveAll(sensor => sensor.Category == SensorCategory.Actuator);
        }
        if (pausedSensorIds is not null)
        {
            sensors.RemoveAll(sensor => pausedSensorIds.Contains(sensor.Id));
        }
        var random = historical ? new Random(7312) : Random.Shared;
        var now = DateTimeOffset.UtcNow;

        foreach (var sensor in sensors)
        {
            for (var index = 0; index < packetsPerSensor; index++)
            {
                var timestamp = historical ? now.AddSeconds(index - packetsPerSensor) : DateTimeOffset.UtcNow;
                var anomaly = historical && index == Math.Max(0, packetsPerSensor - 10);

                switch (sensor.Category)
                {
                    case SensorCategory.Environmental:
                        await PostAsync(client, "environmental", new TelemetryPacket<float>(
                            sensor.Id, anomaly ? 44f : 22f + random.NextSingle() * 8f,
                            timestamp, "°C"), cancellationToken);
                        break;
                    case SensorCategory.PowerConsumption:
                        await PostAsync(client, "power", new TelemetryPacket<int>(
                            sensor.Id, anomaly ? 4400 : random.Next(1000, 1800),
                            timestamp, "W"), cancellationToken);
                        break;
                    case SensorCategory.Actuator:
                        var state = historical ? null : sensor.ActuatorState;
                        await PostAsync(client, "actuator", new TelemetryPacket<bool>(
                            sensor.Id, historical ? index % 2 == 0 : state?.IsOn ?? true,
                            historical ? timestamp.AddMinutes(-2) : timestamp, "state",
                            state?.Version), cancellationToken);
                        break;
                }
            }
        }

        return new SimulationSeedResult(sensors.Count, sensors.Count * packetsPerSensor);
    }

    private void EnsureDemoMesh(IReadOnlyList<SensorResponse> sensors)
    {
        foreach (var sensor in sensors)
        {
            _meshRepository.AddNode(new MeshNode(sensor.Id, sensor.DeviceIdentifier));
        }

        var climate = sensors.Single(sensor => sensor.Category == SensorCategory.Environmental).Id;
        var meter = sensors.Single(sensor => sensor.Category == SensorCategory.PowerConsumption).Id;
        var relay = sensors.Single(sensor => sensor.Category == SensorCategory.Actuator).Id;
        _meshRepository.AddLink(_meshRepository.GatewayId, climate, 1);
        _meshRepository.AddLink(climate, relay, 2);
        _meshRepository.AddLink(relay, meter, 3);
        _meshRepository.AddLink(climate, meter, 6);
        _meshRepository.AddLink(_meshRepository.GatewayId, meter, 8);
    }

    private static async Task<TelemetryAcceptanceResponse> PostAsync<T>(
        HttpClient client,
        string category,
        TelemetryPacket<T> packet,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/telemetry/{category}", packet, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TelemetryAcceptanceResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The API did not return an acceptance receipt.");
    }

    public async Task<SimulationScenarioResult> SendScenarioAsync(
        SimulationScenario scenario, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        using var client = _clientFactory.CreateClient("SimulatedDevices");
        var sensors = await EnsureDemoSensorsAsync(client, cancellationToken);
        var environmental = sensors.Single(sensor => sensor.Category == SensorCategory.Environmental);
        var meter = sensors.Single(sensor => sensor.Category == SensorCategory.PowerConsumption);
        var relay = sensors.Single(sensor => sensor.Category == SensorCategory.Actuator);
        List<Guid> packets = [];
        var now = DateTimeOffset.UtcNow;

        if (scenario == SimulationScenario.PowerSpike)
        {
            for (var index = 0; index < 20; index++)
            {
                var receipt = await PostAsync(client, "environmental",
                    new TelemetryPacket<float>(environmental.Id, 22f, now, "°C"), cancellationToken);
                packets.Add(receipt.PacketId);
            }

            var spike = await PostAsync(client, "power",
                new TelemetryPacket<int>(meter.Id, 4400, DateTimeOffset.UtcNow, "W"), cancellationToken);
            packets.Add(spike.PacketId);
        }
        else
        {
            packets.Add((await PostAsync(client, "environmental",
                new TelemetryPacket<float>(environmental.Id, 22f, now, "°C"), cancellationToken)).PacketId);
            for (var index = 0; index < 2; index++)
            {
                packets.Add((await PostAsync(client, "power",
                    new TelemetryPacket<int>(meter.Id, 1200, now.AddTicks(index), "W"), cancellationToken)).PacketId);
            }
            var relayValue = scenario == SimulationScenario.Normal ? true : relay.ActuatorState?.IsOn ?? true;
            packets.Add((await PostAsync(client, "actuator",
                new TelemetryPacket<bool>(relay.Id, relayValue, now, "state", relay.ActuatorState?.Version), cancellationToken)).PacketId);
        }

        var message = scenario switch
        {
            SimulationScenario.PowerSpike => "Queued 20 normal readings followed by a 4400 W spike. Queue latency includes the demonstration hold. Live simulation is stopped to preserve the result.",
            SimulationScenario.Disconnect => "Relay heartbeats are paused. Its disconnect warning appears after 30 seconds; the other devices continue sending.",
            SimulationScenario.Reconnect => "Relay heartbeats have resumed. Its disconnect warning clears after processing.",
            _ => "Normal traffic started with a 1200 W meter and its relay switched on."
        };
        return new SimulationScenarioResult(scenario, meter.Id, relay.Id, packets, message);
    }

    private static async Task<List<SensorResponse>> EnsureDemoSensorsAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var existing = await client.GetFromJsonAsync<List<SensorResponse>>(
            "api/sensors", JsonOptions, cancellationToken) ?? [];
        RegisterSensorRequest[] registrations =
        [
            new()
            {
                DeviceIdentifier = "ESP32-ENV-01", Facility = "Hydroponics Facility",
                Zone = "Growing Zone A", SubZone = "Bed 1", Node = "Climate Node",
                Category = SensorCategory.Environmental
            },
            new()
            {
                DeviceIdentifier = "ESP32-PWR-01", Facility = "Smart Grid Facility",
                Zone = "Distribution Zone", SubZone = "Panel A", Node = "Power Meter",
                Category = SensorCategory.PowerConsumption
            },
            new()
            {
                DeviceIdentifier = "ESP32-ACT-01", Facility = "Smart Grid Facility",
                Zone = "Distribution Zone", SubZone = "Panel A", Node = "Power Isolation Relay",
                Category = SensorCategory.Actuator
            }
        ];
        List<SensorResponse> sensors = [];

        foreach (var registration in registrations)
        {
            var sensor = existing.Find(item => item.DeviceIdentifier.Equals(
                registration.DeviceIdentifier, StringComparison.OrdinalIgnoreCase));

            if (sensor is null)
            {
                using var response = await client.PostAsJsonAsync("api/sensors", registration, cancellationToken);

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    existing = await client.GetFromJsonAsync<List<SensorResponse>>(
                        "api/sensors", JsonOptions, cancellationToken) ?? [];
                    sensor = existing.Find(item => item.DeviceIdentifier.Equals(
                        registration.DeviceIdentifier, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    sensor = await response.Content.ReadFromJsonAsync<SensorResponse>(JsonOptions, cancellationToken);
                }
            }

            if (sensor is null || sensor.Category != registration.Category)
            {
                throw new InvalidOperationException("A demo device could not be registered with its expected category.");
            }

            sensors.Add(sensor);
        }

        return sensors;
    }
}
