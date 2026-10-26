using Microsoft.AspNetCore.Mvc;
using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AttachmentsController : ControllerBase
{
    private readonly AttachmentService _attachmentService;

    public AttachmentsController(
        AttachmentService attachmentService)
    {
        _attachmentService = attachmentService;
    }

    [HttpGet("sensor/{sensorId:guid}")]
    public ActionResult<IReadOnlyList<AttachmentResponse>>
        GetBySensor(Guid sensorId)
    {
        return Ok(
            _attachmentService.GetBySensor(sensorId));
    }

    [HttpPost("sensor/{sensorId:guid}")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<ActionResult<AttachmentResponse>> Upload(
        Guid sensorId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        try
        {
            var attachment =
                await _attachmentService.UploadAsync(
                    sensorId,
                    file,
                    cancellationToken);

            return Ok(attachment);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("{attachmentId:guid}/download")]
    public async Task<IActionResult> Download(
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result =
                await _attachmentService.DownloadAsync(
                    attachmentId,
                    cancellationToken);

            return File(
                result.Content,
                result.Record.ContentType,
                result.Record.OriginalFileName);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
    }
}
