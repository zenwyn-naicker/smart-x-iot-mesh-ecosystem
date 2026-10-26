using Microsoft.AspNetCore.DataProtection;
using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class AttachmentService
{
    private const long MaximumFileSize = 10 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".txt",
            ".log",
            ".json",
            ".cfg"
        };

    private readonly ISensorRepository _sensorRepository;
    private readonly IDataProtector _protector;
    private readonly string _uploadDirectory;
    private readonly List<AttachmentRecord> _records = [];
    private readonly object _sync = new();

    public AttachmentService(
        ISensorRepository sensorRepository,
        IDataProtectionProvider provider,
        IWebHostEnvironment environment)
    {
        _sensorRepository = sensorRepository;
        _protector = provider.CreateProtector(
            "SmartX.SensorAttachments.v1");

        _uploadDirectory = Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "uploads");

        Directory.CreateDirectory(_uploadDirectory);
    }

    public async Task<AttachmentResponse> UploadAsync(
        Guid sensorId,
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (_sensorRepository.GetById(sensorId) is null)
        {
            throw new KeyNotFoundException(
                "The selected sensor does not exist.");
        }

        if (file is null || file.Length == 0)
        {
            throw new ArgumentException(
                "Choose a non-empty file.");
        }

        if (file.Length > MaximumFileSize)
        {
            throw new ArgumentException(
                "Files may not exceed 10 MB.");
        }

        var originalFileName =
            Path.GetFileName(file.FileName);

        var extension =
            Path.GetExtension(originalFileName);

        if (!AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException(
                "This file type is not permitted.");
        }

        await using var input = file.OpenReadStream();
        await using var memory = new MemoryStream();

        await input.CopyToAsync(
            memory,
            cancellationToken);

        var encrypted = _protector.Protect(
            memory.ToArray());

        var storedPath = Path.Combine(
            _uploadDirectory,
            $"{Guid.NewGuid():N}.protected");

        await File.WriteAllBytesAsync(
            storedPath,
            encrypted,
            cancellationToken);

        var record = new AttachmentRecord
        {
            SensorId = sensorId,
            OriginalFileName = originalFileName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType,
            StoredPath = storedPath,
            Length = file.Length,
            UploadedAt = DateTimeOffset.UtcNow
        };

        lock (_sync)
        {
            _records.Add(record);
        }

        return ToResponse(record);
    }

    public IReadOnlyList<AttachmentResponse> GetBySensor(
        Guid sensorId)
    {
        lock (_sync)
        {
            return _records
                .Where(record =>
                    record.SensorId == sensorId)
                .OrderByDescending(record =>
                    record.UploadedAt)
                .Select(ToResponse)
                .ToList();
        }
    }

    public async Task<(byte[] Content, AttachmentRecord Record)>
        DownloadAsync(
            Guid attachmentId,
            CancellationToken cancellationToken)
    {
        AttachmentRecord? record;

        lock (_sync)
        {
            record = _records.FirstOrDefault(item =>
                item.Id == attachmentId);
        }

        if (record is null)
        {
            throw new KeyNotFoundException(
                "The attachment does not exist.");
        }

        var encrypted = await File.ReadAllBytesAsync(
            record.StoredPath,
            cancellationToken);

        var decrypted = _protector.Unprotect(encrypted);

        return (decrypted, record);
    }

    private static AttachmentResponse ToResponse(
        AttachmentRecord record)
    {
        return new AttachmentResponse(
            record.Id,
            record.SensorId,
            record.OriginalFileName,
            record.ContentType,
            record.Length,
            record.UploadedAt);
    }
}
