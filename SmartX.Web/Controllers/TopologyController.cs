using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Services;
using SmartX.Web.Models;

namespace SmartX.Web.Controllers;

public sealed class TopologyController(SmartXApiClient apiClient) : Controller
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken, string? search = null, bool snapshot = false)
    {
        try
        {
            var model = await apiClient.GetTopologyAsync(cancellationToken, search);
            model.Simulation = await apiClient.GetSimulationStatusAsync(cancellationToken);
            return snapshot ? PartialView("Index", model) : View(model);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (snapshot)
            {
                return StatusCode(503);
            }
            return View("ApiUnavailable", "The network topology is unavailable. Check the API connection and try again.");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ChangeLink(Guid firstNodeId, Guid secondNodeId, string? cost, string operation, string? search,
        CancellationToken cancellationToken)
    {
        var validCost = double.TryParse(cost, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedCost);
        if (!ModelState.IsValid || operation is not ("add" or "update" or "remove") ||
            firstNodeId == Guid.Empty || secondNodeId == Guid.Empty || firstNodeId == secondNodeId ||
            operation != "remove" && (!validCost || !double.IsFinite(parsedCost) || parsedCost <= 0))
        {
            TempData["Error"] = "Select two different nodes and enter a finite cost greater than zero.";
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Index), new { search }));
        }
        var link = new MeshLinkForm(firstNodeId, secondNodeId, parsedCost);
        return Submit(() => apiClient.ChangeMeshLinkAsync(link, operation, cancellationToken),
            "Connection updated.", search, cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Simulation(string operation, string? search, CancellationToken cancellationToken)
    {
        return operation switch
        {
            "load" => Submit(() => apiClient.SeedAsync(250, cancellationToken),
                "Demo connections loaded and sample readings accepted.", search, cancellationToken),
            "start" => Submit(() => apiClient.StartSimulationAsync(1, cancellationToken),
                "Live simulation started. Individual pauses have been cleared.", search, cancellationToken),
            "stop" => Submit(() => apiClient.StopSimulationAsync(cancellationToken),
                "Live simulation stopped. Accepted readings will finish processing.", search, cancellationToken),
            _ => Task.FromResult<IActionResult>(BadRequest("Choose a valid simulation action."))
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PauseSensor(Guid sensorId, bool paused, string? search, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || sensorId == Guid.Empty)
        {
            return Task.FromResult<IActionResult>(BadRequest("Select a demo device."));
        }
        return Submit(() => apiClient.SetDemoSensorPausedAsync(sensorId, paused, cancellationToken),
            paused ? "Device paused. It becomes offline 30 seconds after its last heartbeat."
                : "Device resumed. Its next live reading will restore availability while simulation is running.",
            search, cancellationToken);
    }

    private async Task<IActionResult> Submit(Func<Task> action, string message, string? search,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
            TempData["Success"] = message;
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            TempData["Error"] = ApiError.GetMessage(exception,
                "The action could not be confirmed. Refresh and check the current state before trying again.");
        }
        return RedirectToAction(nameof(Index), new { search });
    }
}
