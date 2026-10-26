using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Models;
using SmartX.Web.Services;

namespace SmartX.Web.Controllers;

public sealed class SensorsController : Controller
{
    private readonly SmartXApiClient _apiClient;

    public SensorsController(SmartXApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IActionResult> Index(
        CancellationToken cancellationToken)
    {
        try
        {
            return View(await _apiClient.GetSensorsAsync(cancellationToken));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "Sensors are unavailable. Check the API connection and try again."));
        }
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View(new RegisterSensorViewModel());
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> History(Guid id, [FromQuery] SensorHistoryFilter filters,
        CancellationToken cancellationToken)
    {
        if (filters.From > filters.To)
        {
            ModelState.AddModelError("From", "The start time must not be after the end time.");
        }

        var model = new SensorHistoryViewModel { Filters = filters };
        try
        {
            var sensor = await _apiClient.GetSensorAsync(id, cancellationToken);
            if (sensor is null)
            {
                return NotFound();
            }
            model.Sensor = sensor;
            if (ModelState.IsValid)
            {
                model.History = await _apiClient.GetSensorHistoryAsync(id, filters, cancellationToken);
            }
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            if (model.Sensor.Id == Guid.Empty)
            {
                return View("ApiUnavailable", "Sensor history is unavailable. Check the API connection and try again.");
            }
            ModelState.AddModelError(string.Empty, "History could not be loaded. Check the API connection and try again.");
        }
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        RegisterSensorViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            await _apiClient.RegisterSensorAsync(
                model,
                cancellationToken);

            TempData["Success"] =
                "The sensor was registered successfully.";

            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            ModelState.AddModelError(
                string.Empty,
                ApiError.GetMessage(exception,
                    "Registration could not be confirmed. Check the sensor list before trying again."));

            return View(model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var sensor = await _apiClient.GetSensorAsync(id, cancellationToken);
            if (sensor is null)
            {
                return NotFound();
            }

            return View(new SensorDetailsPageViewModel
            {
                Sensor = sensor,
                Attachments = await _apiClient.GetAttachmentsAsync(id, cancellationToken)
            });
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "The sensor profile is unavailable. Check the API connection and try again."));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Upload(
        Guid id,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.UploadAttachmentAsync(
                id,
                file,
                cancellationToken);

            TempData["Success"] =
                "The encrypted attachment was uploaded.";
        }
        catch (Exception exception) when (exception is ArgumentException ||
            ApiError.IsExpected(exception, cancellationToken))
        {
            TempData["Error"] = ApiError.GetMessage(exception,
                "The upload could not be confirmed. Check the attachment list before uploading again.");
        }

        return RedirectToAction(
            nameof(Details),
            new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Preview(
        Guid id,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var attachments = await _apiClient.GetAttachmentsAsync(id, cancellationToken);
            var attachment = attachments.FirstOrDefault(item => item.Id == attachmentId);
            if (attachment is null)
            {
                return NotFound();
            }

            var result = await _apiClient.DownloadAttachmentAsync(attachmentId, cancellationToken);
            var model = new AttachmentPreviewViewModel
            {
                SensorId = id,
                AttachmentId = attachmentId,
                FileName = attachment.FileName
            };
            model.Load(result.Content);
            return View(model);
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "The attachment is unavailable. Check the API connection and try again."));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Download(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _apiClient.DownloadAttachmentAsync(attachmentId, cancellationToken);
            return File(result.Content, result.ContentType, result.FileName);
        }
        catch (Exception exception) when (ApiError.IsExpected(exception, cancellationToken))
        {
            return View("ApiUnavailable", ApiError.GetMessage(exception,
                "The attachment is unavailable. Check the API connection and try again."));
        }
    }
}
