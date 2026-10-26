using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Models;
using SmartX.Web.Services;

namespace SmartX.Web.Controllers;

public sealed class TelemetryController : Controller
{
    private readonly SmartXApiClient _apiClient;

    public TelemetryController(SmartXApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
    {
        try
        {
            return View(new TelemetryFormViewModel
            {
                Sensors = await _apiClient.GetSensorsAsync(cancellationToken)
            });
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "Telemetry ingestion is unavailable. Check the API connection and try again."));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(
        TelemetryFormViewModel model,
        CancellationToken cancellationToken)
    {
        try
        {
            model.Sensors = await _apiClient.GetSensorsAsync(cancellationToken);
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            model.SensorsUnavailable = true;
            ModelState.AddModelError(string.Empty, ApiError.GetMessage(exception,
                "Sensors could not be loaded. Your reading has not been sent. Check the API connection and try again."));
            return View(model);
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var sensor = model.Sensors.FirstOrDefault(item =>
            item.Id == model.SensorId);

        if (sensor is null)
        {
            ModelState.AddModelError(
                nameof(model.SensorId),
                "Select a registered sensor.");

            return View(model);
        }

        try
        {
            switch (sensor.Category)
            {
                case SensorCategory.Environmental:
                    if (!float.TryParse(
                        model.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var environmentalValue) || !float.IsFinite(environmentalValue))
                    {
                        ModelState.AddModelError(nameof(model.Value), "Enter a finite decimal value.");
                        return View(model);
                    }

                    await _apiClient.SendEnvironmentalAsync(
                        sensor.Id,
                        environmentalValue,
                        cancellationToken);
                    break;

                case SensorCategory.PowerConsumption:
                    if (!int.TryParse(
                        model.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var powerValue))
                    {
                        ModelState.AddModelError(nameof(model.Value), "Enter a valid whole-number wattage.");
                        return View(model);
                    }

                    await _apiClient.SendPowerAsync(
                        sensor.Id,
                        powerValue,
                        cancellationToken);
                    break;

                case SensorCategory.Actuator:
                    if (!bool.TryParse(
                        model.Value,
                        out var actuatorValue))
                    {
                        ModelState.AddModelError(nameof(model.Value), "Enter true or false.");
                        return View(model);
                    }

                    await _apiClient.SendActuatorAsync(
                        sensor.Id,
                        actuatorValue,
                        cancellationToken);
                    break;
                default:
                    ModelState.AddModelError(nameof(model.SensorId), "Select a sensor with a valid category.");
                    return View(model);
            }

            TempData["Success"] =
                "Telemetry accepted for processing. The dashboard will update once processed.";

            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            ModelState.AddModelError(
                string.Empty,
                ApiError.GetMessage(exception,
                    "The submission could not be confirmed. Check the dashboard before sending the reading again."));

            return View(model);
        }
    }
}
