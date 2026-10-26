using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SuggestionsController(ActionSuggestionService service) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ActionSuggestion>> Get([FromQuery, Range(1, 20)] int limit = 5)
    {
        return Ok(service.GetSuggestions(limit));
    }
}
