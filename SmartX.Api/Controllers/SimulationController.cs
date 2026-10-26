using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Services;
using SmartX.Api.Models;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SimulationController : ControllerBase
{
    private readonly DemoDataService _demoDataService;
    private readonly SimulationWorker _worker;

    public SimulationController(
        DemoDataService demoDataService,
        SimulationWorker worker)
    {
        _demoDataService = demoDataService;
        _worker = worker;
    }

    [HttpPost("seed")]
    public async Task<ActionResult<SimulationSeedResult>> Seed(
        CancellationToken cancellationToken,
        [FromQuery] int packetsPerSensor = 250)
    {
        packetsPerSensor = Math.Clamp(
            packetsPerSensor,
            10,
            5000);

        try
        {
            return Ok(await _demoDataService.SeedAsync(packetsPerSensor, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Sample loading stopped because a device request failed. Some readings may already be accepted." });
        }
    }

    [HttpGet("status")]
    public ActionResult<SimulationStatus> Status() => Ok(_worker.GetStatus());

    [HttpPost("devices/{sensorId:guid}/pause")]
    public Task<ActionResult<SimulationStatus>> PauseSensor(Guid sensorId, CancellationToken cancellationToken)
    {
        return SetSensorPaused(sensorId, true, cancellationToken);
    }

    [HttpPost("devices/{sensorId:guid}/resume")]
    public Task<ActionResult<SimulationStatus>> ResumeSensor(Guid sensorId, CancellationToken cancellationToken)
    {
        return SetSensorPaused(sensorId, false, cancellationToken);
    }

    private async Task<ActionResult<SimulationStatus>> SetSensorPaused(
        Guid sensorId, bool paused, CancellationToken cancellationToken)
    {
        if (sensorId == Guid.Empty)
        {
            return BadRequest(new { message = "Provide a sensor ID." });
        }

        try
        {
            return Ok(await _worker.SetSensorPausedAsync(sensorId, paused, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("scenario")]
    public async Task<ActionResult<SimulationScenarioResult>> Scenario(
        SimulationScenarioRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _worker.RunScenarioAsync(request.Scenario!.Value, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Scenario stopped before all readings were accepted. Processing has resumed; check simulation status." });
        }
    }

    [HttpPost("start")]
    public async Task<ActionResult<SimulationStatus>> Start(
        StartSimulationRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _worker.StartSimulationAsync(request.ReadingsPerSecond, cancellationToken));
    }

    [HttpPost("stop")]
    public async Task<ActionResult<SimulationStatus>> Stop(CancellationToken cancellationToken)
    {
        return Ok(await _worker.StopSimulationAsync(cancellationToken));
    }
}
