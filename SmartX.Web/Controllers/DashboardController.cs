using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Services;

namespace SmartX.Web.Controllers;

public sealed class DashboardController : Controller
{
    private readonly SmartXApiClient _apiClient;

    public DashboardController(SmartXApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
    {
        try
        {
            return View(await _apiClient.GetDashboardAsync(cancellationToken));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "The dashboard is unavailable. Check the API connection and try again."));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Data(
        CancellationToken cancellationToken)
    {
        try
        {
            return Json(await _apiClient.GetDashboardAsync(cancellationToken));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Seed(
        CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.SeedAsync(250, cancellationToken);
            TempData["Success"] = "Sample telemetry accepted for processing.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TempData["Error"] = "Sample loading could not finish. Some readings may already be accepted. Check the API and try again.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> SimulationStatus(CancellationToken cancellationToken)
    {
        try
        {
            return Json(await _apiClient.GetSimulationStatusAsync(cancellationToken));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartSimulation(int readingsPerSecond, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || readingsPerSecond is < 1 or > 20)
        {
            TempData["Error"] = "Choose between 1 and 20 readings per device per second.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _apiClient.StartSimulationAsync(readingsPerSecond, cancellationToken);
            TempData["Success"] = "Live simulation started. It will continue when you leave this page.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TempData["Error"] = "Unable to start simulation. Check the API and try again.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StopSimulation(CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.StopSimulationAsync(cancellationToken);
            TempData["Success"] = "Live simulation stopped. Already accepted readings will finish processing.";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TempData["Error"] = "Unable to confirm simulation stopped. Check the API and try again.";
        }

        return RedirectToAction(nameof(Index));
    }
}
