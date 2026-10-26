using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TopologyController : ControllerBase
{
    private readonly MeshTopologyService _topologyService;

    public TopologyController(MeshTopologyService topologyService)
    {
        _topologyService = topologyService;
    }

    [HttpGet]
    public ActionResult<MeshGraph> Graph()
    {
        return Ok(_topologyService.GetGraph());
    }

    [HttpGet("reachability")]
    public ActionResult<MeshReachability> Reachability([FromQuery] Guid? startNodeId)
    {
        return ForNode(startNodeId, _topologyService.GetReachability);
    }

    [HttpGet("spanning-tree")]
    public ActionResult<MeshSpanningTree> SpanningTree([FromQuery] Guid? startNodeId)
    {
        return ForNode(startNodeId, _topologyService.GetMinimumSpanningTree);
    }

    [HttpGet("route")]
    public ActionResult<MeshRoute> Route([FromQuery] Guid? startNodeId)
    {
        return ForNode(startNodeId, _topologyService.GetGatewayRoute);
    }

    [HttpPost("links")]
    public IActionResult AddLink(MeshLinkRequest request)
    {
        return ChangeLink(() => _topologyService.AddLink(request.FirstNodeId, request.SecondNodeId, request.Cost),
            true);
    }

    [HttpPut("links")]
    public IActionResult UpdateLink(MeshLinkRequest request)
    {
        return ChangeLink(() => _topologyService.UpdateLink(request.FirstNodeId, request.SecondNodeId, request.Cost),
            false);
    }

    [HttpDelete("links")]
    public IActionResult RemoveLink([FromQuery] Guid firstNodeId, [FromQuery] Guid secondNodeId)
    {
        return ChangeLink(() => _topologyService.RemoveLink(firstNodeId, secondNodeId), false);
    }

    private IActionResult ChangeLink(Func<bool> operation, bool adding)
    {
        try
        {
            if (operation())
            {
                return NoContent();
            }
            return adding ? Conflict(new { message = "This connection already exists." })
                : NotFound(new { message = "This connection does not exist." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "The selected mesh node does not exist." });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Select two different nodes and enter a cost greater than zero and no more than 1,000,000." });
        }
    }

    private ActionResult<T> ForNode<T>(Guid? nodeId, Func<Guid, T> operation)
    {
        if (nodeId is null || nodeId == Guid.Empty)
        {
            return BadRequest(new { message = "Provide a mesh node ID." });
        }

        try
        {
            return Ok(operation(nodeId.Value));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "The selected mesh node does not exist." });
        }
    }
}
