using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.DataStructures;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TelemetryController : ControllerBase
{
    private readonly TelemetryService _telemetryService;
    private readonly TelemetryProcessingWorker _processingWorker;
    private readonly TelemetryQueue _queue;
    private readonly ISensorRepository _sensorRepository;

    public TelemetryController(
        TelemetryService telemetryService,
        TelemetryProcessingWorker processingWorker,
        TelemetryQueue queue,
        ISensorRepository sensorRepository)
    {
        _telemetryService = telemetryService;
        _processingWorker = processingWorker;
        _queue = queue;
        _sensorRepository = sensorRepository;
    }

    [HttpGet("processing")]
    public ActionResult<TelemetryProcessingStatus> Processing()
    {
        return Ok(_processingWorker.GetStatus());
    }

    [HttpPost("environmental")]
    public ActionResult<TelemetryAcceptanceResponse> Environmental(
        TelemetryPacket<float> packet)
    {
        if (packet is not null && !float.IsFinite(packet.Value))
        {
            return BadRequest(new { message = "Enter a finite environmental value." });
        }

        return Enqueue(packet, SensorCategory.Environmental,
            value => new TelemetryWorkItem.Environmental(value));
    }

    [HttpPost("power")]
    public ActionResult<TelemetryAcceptanceResponse> Power(
        TelemetryPacket<int> packet)
    {
        return Enqueue(packet, SensorCategory.PowerConsumption,
            value => new TelemetryWorkItem.Power(value));
    }

    [HttpPost("actuator")]
    public ActionResult<TelemetryAcceptanceResponse> Actuator(
        TelemetryPacket<bool> packet)
    {
        return Enqueue(packet, SensorCategory.Actuator,
            value => new TelemetryWorkItem.Actuator(value));
    }

    [HttpGet("history")]
    public ActionResult<IReadOnlyList<TelemetryReadingResponse>>
        History(
            [FromQuery] Guid? sensorId,
            [FromQuery] int limit = 200)
    {
        return Ok(
            _telemetryService.GetHistory(sensorId, limit));
    }

    [HttpGet("power/aggregate")]
    public ActionResult<object> AggregatePower()
    {
        var aggregate =
            _telemetryService.GetAggregatePower();

        return Ok(new
        {
            aggregateWatts = aggregate.Watts
        });
    }

    [HttpGet("history/range")]
    public ActionResult<TelemetryHistoryRangeResponse> HistoryRange(
        [FromQuery] Guid? sensorId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 200)
    {
        if (sensorId is null || sensorId == Guid.Empty || from is null || to is null)
        {
            return BadRequest(new { message = "Provide a sensor ID, start time and end time." });
        }

        if (from > to)
        {
            return BadRequest(new { message = "The start time must not be after the end time." });
        }

        if (limit is < 1 or > 1000)
        {
            return BadRequest(new { message = "Choose a limit between 1 and 1000." });
        }

        if (_sensorRepository.GetById(sensorId.Value) is null)
        {
            return NotFound(new { message = "The selected sensor does not exist." });
        }

        return Ok(_telemetryService.GetHistoryRange(sensorId.Value, from.Value, to.Value, limit));
    }

    private ActionResult<TelemetryAcceptanceResponse> Enqueue<T>(
        TelemetryPacket<T>? packet,
        SensorCategory expectedCategory,
        Func<TelemetryPacket<T>, TelemetryWorkItem> createItem)
    {
        if (packet is null || packet.SensorId == Guid.Empty ||
            packet.Timestamp == default || string.IsNullOrWhiteSpace(packet.Unit) ||
            packet.Unit.Length > 32)
        {
            return BadRequest(new
            {
                message = "Provide a sensor ID, timestamp and a unit of up to 32 characters."
            });
        }

        var sensor = _sensorRepository.GetById(packet.SensorId);

        if (sensor is null)
        {
            return NotFound(new { message = "The selected sensor does not exist." });
        }

        if (sensor.Category != expectedCategory)
        {
            return BadRequest(new
            {
                message = $"This endpoint only accepts {expectedCategory} sensors."
            });
        }

        var item = createItem(packet);
        if (!_queue.TryEnqueue(item))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "The telemetry queue is full. Try again shortly."
            });
        }

        return Accepted(new TelemetryAcceptanceResponse(
            packet.SensorId, item.EnqueuedAt, "Telemetry accepted for processing.", item.Id));
    }

    [HttpGet("processing/history")]
    public ActionResult<IReadOnlyList<TelemetryProcessingEvent>> ProcessingHistory()
    {
        return Ok(_processingWorker.GetRecentProcessing());
    }
}
