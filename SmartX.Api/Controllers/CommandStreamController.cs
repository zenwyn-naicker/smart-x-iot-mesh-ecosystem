using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/command-stream")]
public sealed class CommandStreamController(
    CommandStreamService service, UserActivityService activityService) : ControllerBase
{
    [HttpGet]
    public ActionResult<CommandStreamResponse> Get([FromQuery] CommandStreamQuery query)
    {
        return Ok(service.GetStream(query));
    }

    [HttpPost("search")]
    public ActionResult<CommandStreamResponse> Search(CommandStreamQuery query)
    {
        var result = service.GetStream(query);
        activityService.RecordSearch(query, result.Devices.Select(device => device.Id));
        return Ok(result);
    }
}
