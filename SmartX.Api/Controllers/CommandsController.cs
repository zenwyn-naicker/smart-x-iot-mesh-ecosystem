using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CommandsController(
    DeviceCommandService commands, ISensorRepository sensors) : ControllerBase
{
    [HttpGet("history")]
    public ActionResult<IReadOnlyList<DeviceCommandResult>> History(
        [FromQuery, Range(1, 1000)] int limit = 100)
    {
        return Ok(commands.GetHistory(limit));
    }

    [HttpPost]
    public ActionResult<DeviceCommandResult> Execute(DeviceCommandRequest request)
    {
        if (request.RequestId == Guid.Empty || request.SensorId == Guid.Empty)
        {
            return BadRequest(new { message = "Non-empty request and sensor IDs are required." });
        }

        var sensor = sensors.GetById(request.SensorId);
        if (sensor is null)
        {
            return NotFound(new { message = "The selected sensor does not exist." });
        }

        if (sensor.Category != SensorCategory.Actuator)
        {
            return BadRequest(new { message = "Only actuators support On/Off commands." });
        }

        return Respond(() => commands.Execute(request.RequestId, request.SensorId, request.IsOn!.Value));
    }

    [HttpPost("undo")]
    public ActionResult<DeviceCommandResult> Undo(UndoCommandRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return BadRequest(new { message = "A non-empty request ID is required." });
        }

        return Respond(() => commands.Undo(request.RequestId));
    }

    private ActionResult<DeviceCommandResult> Respond(Func<DeviceCommandResult?> action)
    {
        try
        {
            var result = action();
            if (result is null)
            {
                return Conflict(new { message = "There are no commands to undo." });
            }

            return result.Outcome == DeviceCommandOutcome.Rejected ? Conflict(result) : Ok(result);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
    }
}
