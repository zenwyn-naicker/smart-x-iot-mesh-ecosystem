using SmartX.Api.Models;
using SmartX.Api.DataStructures;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class SimulationWorker : BackgroundService
{
    private readonly DemoDataService _demoDataService;
    private readonly ILogger<SimulationWorker> _logger;
    private readonly TelemetryQueue _queue;
    private readonly ISensorRepository _sensorRepository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SimulationStatus _status = new(false, 1, null);

    public SimulationWorker(DemoDataService demoDataService, ILogger<SimulationWorker> logger,
        TelemetryQueue queue, ISensorRepository sensorRepository)
    {
        _demoDataService = demoDataService;
        _logger = logger;
        _queue = queue;
        _sensorRepository = sensorRepository;
    }

    public SimulationStatus GetStatus() => _status;

    public async Task<SimulationStatus> SetSensorPausedAsync(
        Guid sensorId, bool paused, CancellationToken cancellationToken)
    {
        var sensor = _sensorRepository.GetById(sensorId)
            ?? throw new KeyNotFoundException("The selected sensor does not exist.");
        var identifier = sensor.Category switch
        {
            SensorCategory.Environmental => "ESP32-ENV-01",
            SensorCategory.PowerConsumption => "ESP32-PWR-01",
            SensorCategory.Actuator => "ESP32-ACT-01",
            _ => null
        };
        if (!string.Equals(sensor.DeviceIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only demo devices can be paused or resumed.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var ids = _status.PausedSensorIds.ToList();
            ids.Remove(sensorId);
            if (paused)
            {
                ids.Add(sensorId);
            }

            _status = _status with
            {
                PausedSensorIds = ids.AsReadOnly(),
                RelayDisconnected = sensor.Category == SensorCategory.Actuator ? paused : _status.RelayDisconnected
            };
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationStatus> StartSimulationAsync(int readingsPerSecond, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readingsPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readingsPerSecond, 20);
        await _gate.WaitAsync(cancellationToken);

        try
        {
            _status = _status with
            {
                IsRunning = true, ReadingsPerSecond = readingsPerSecond, Error = null,
                RelayDisconnected = false, PausedSensorIds = Array.Empty<Guid>()
            };
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationStatus> StopSimulationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            _status = _status with { IsRunning = false };
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationScenarioResult> RunScenarioAsync(
        SimulationScenario scenario, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(scenario))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await _gate.WaitAsync(cancellationToken);
        var pauseProcessing = scenario == SimulationScenario.PowerSpike;

        try
        {
            _status = _status with { IsRunning = false, Error = null };
            if (pauseProcessing)
            {
                _queue.PauseProcessing();
            }

            var result = await _demoDataService.SendScenarioAsync(scenario, cancellationToken);
            _status = _status with
            {
                IsRunning = !pauseProcessing,
                ReadingsPerSecond = 1,
                RelayDisconnected = scenario == SimulationScenario.Disconnect,
                PausedSensorIds = scenario == SimulationScenario.Disconnect
                    ? Array.AsReadOnly(new[] { result.RelayId }) : Array.Empty<Guid>()
            };
            return result;
        }
        catch
        {
            _status = _status with { IsRunning = false, Error = "Scenario stopped before all readings were accepted." };
            throw;
        }
        finally
        {
            if (pauseProcessing)
            {
                _queue.ResumeProcessing();
            }
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _gate.WaitAsync(stoppingToken);

                try
                {
                    if (!_status.IsRunning)
                    {
                        continue;
                    }

                    await _demoDataService.SendLiveAsync(
                        _status.ReadingsPerSecond, stoppingToken, _status.RelayDisconnected, _status.PausedSensorIds);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _status = _status with { IsRunning = false, Error = "Simulation stopped because a device request failed." };
                    _logger.LogError(exception, "Device simulation failed.");
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _status = _status with { IsRunning = false };
        }
    }
}
