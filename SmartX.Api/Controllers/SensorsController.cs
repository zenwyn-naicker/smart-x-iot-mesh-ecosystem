using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SensorsController : ControllerBase
{
    private readonly SensorService _sensorService;

    public SensorsController(SensorService sensorService)
    {
        _sensorService = sensorService;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<SensorResponse>> GetAll()
    {
        return Ok(_sensorService.GetAll());
    }

    [HttpGet("{id:guid}")]
    public ActionResult<SensorResponse> GetById(Guid id)
    {
        var sensor = _sensorService.GetById(id);

        return sensor is null
            ? NotFound()
            : Ok(sensor);
    }

    [HttpPost]
    public ActionResult<SensorResponse> Register(
        RegisterSensorRequest request)
    {
        try
        {
            var sensor = _sensorService.Register(request);

            return CreatedAtAction(
                nameof(GetById),
                new { id = sensor.Id },
                sensor);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { message = exception.Message });
        }
    }
}