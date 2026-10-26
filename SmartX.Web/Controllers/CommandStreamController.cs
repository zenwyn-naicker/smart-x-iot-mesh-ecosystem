using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Models;
using SmartX.Web.Services;

namespace SmartX.Web.Controllers;

public sealed class CommandStreamController(SmartXApiClient apiClient) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Execute([FromForm] DeviceCommandForm command,
        [FromQuery] CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || command.RequestId == Guid.Empty || command.SensorId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(BadRequest("Invalid command request."));
        }

        return SubmitCommand(() => apiClient.ExecuteCommandAsync(command, cancellationToken), filters, cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Undo([FromForm] UndoCommandForm command,
        [FromQuery] CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || command.RequestId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(BadRequest("Invalid Undo request."));
        }

        return SubmitCommand(() => apiClient.UndoCommandAsync(command, cancellationToken), filters, cancellationToken);
    }

    private async Task<IActionResult> SubmitCommand(Func<Task<CommandFeedback>> action,
        CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        try
        {
            var result = await action();
            TempData[result.Succeeded ? "Success" : "Error"] = result.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TempData["Error"] = "The command result could not be confirmed. Check command history and device state before trying again.";
        }

        return RedirectToAction(nameof(Index), new { filters.Search, filters.Category, filters.Alert });
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Data([FromQuery] CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("Invalid command stream filters.");
        }

        try
        {
            return PartialView("_Stream", await apiClient.GetCommandStreamAsync(filters, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("Invalid command stream filters.");
        }

        try
        {
            return View(await apiClient.GetCommandStreamAsync(filters, cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return View(new CommandStreamViewModel
            {
                Filters = filters,
                Error = "The command stream is unavailable. Check the API connection and try again."
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Search(CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest("Invalid command stream filters.");
        }

        filters.Search = filters.Search?.Trim();
        try
        {
            await apiClient.SearchCommandStreamAsync(filters, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TempData["Error"] = "Your search could not be recorded. The selected filters will still be applied when the API is available.";
        }

        return RedirectToAction(nameof(Index), new { filters.Search, filters.Category, filters.Alert });
    }
}
