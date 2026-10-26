using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartX.Web.Models;

namespace SmartX.Web.Services;

public sealed class SmartXApiClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

    private readonly HttpClient _httpClient;

    public async Task ChangeMeshLinkAsync(MeshLinkForm link, string operation, CancellationToken cancellationToken)
    {
        using var response = operation switch
        {
            "add" => await _httpClient.PostAsJsonAsync("api/topology/links", link, cancellationToken),
            "update" => await _httpClient.PutAsJsonAsync("api/topology/links", link, cancellationToken),
            "remove" => await _httpClient.DeleteAsync(
                $"api/topology/links?firstNodeId={link.FirstNodeId}&secondNodeId={link.SecondNodeId}", cancellationToken),
            _ => throw new ArgumentException("Choose a valid connection action.", nameof(operation))
        };
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetDemoSensorPausedAsync(Guid sensorId, bool paused, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(
            $"api/simulation/devices/{sensorId}/{(paused ? "pause" : "resume")}", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<SensorHistoryRangeViewModel> GetSensorHistoryAsync(
        Guid sensorId, SensorHistoryFilter filters, CancellationToken cancellationToken)
    {
        var route = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("api/telemetry/history/range",
            new Dictionary<string, string?>
            {
                ["sensorId"] = sensorId.ToString(),
                ["from"] = DateTime.SpecifyKind(filters.From!.Value, DateTimeKind.Utc).ToString("O"),
                ["to"] = DateTime.SpecifyKind(filters.To!.Value, DateTimeKind.Utc).ToString("O"),
                ["limit"] = filters.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        return await _httpClient.GetFromJsonAsync<SensorHistoryRangeViewModel>(route, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The history response was empty.");
    }

    public async Task<TopologyViewModel> GetTopologyAsync(CancellationToken cancellationToken = default, string? search = null)
    {
        var model = await _httpClient.GetFromJsonAsync<TopologyViewModel>("api/topology", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The topology response was empty.");
        var gateway = model.Nodes.Values.Single(node => node.IsGateway);
        model.Reachability = await _httpClient.GetFromJsonAsync<MeshReachabilityViewModel>(
            $"api/topology/reachability?startNodeId={gateway.Id}", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The reachability response was empty.");
        model.Search = search?.Trim();
        if (!string.IsNullOrEmpty(model.Search))
        {
            var exact = model.Nodes.Values.FirstOrDefault(node =>
                node.Identifier.Equals(model.Search, StringComparison.OrdinalIgnoreCase));
            model.Matches = exact is not null ? [exact] : model.Nodes.Values
                .Where(node => node.Identifier.Contains(model.Search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => node.Identifier).ToList();
            if (model.Matches.Count == 1)
            {
                model.Route = await _httpClient.GetFromJsonAsync<MeshRouteViewModel>(
                    $"api/topology/route?startNodeId={model.Matches[0].Id}", JsonOptions, cancellationToken)
                    ?? throw new InvalidOperationException("The route response was empty.");
            }
        }
        return model;
    }

    public async Task<CommandStreamViewModel> GetCommandStreamAsync(
        CommandStreamFilter filters, CancellationToken cancellationToken = default)
    {
        var route = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("api/command-stream",
            new Dictionary<string, string?>
            {
                ["search"] = filters.Search,
                ["category"] = filters.Category?.ToString(),
                ["alert"] = filters.Alert.ToString()
            });
        var streamTask = _httpClient.GetFromJsonAsync<CommandStreamViewModel>(route, JsonOptions, cancellationToken);
        var historyTask = _httpClient.GetFromJsonAsync<List<DeviceCommandViewModel>>(
            "api/commands/history", JsonOptions, cancellationToken);
        var sensorsTask = GetSensorsAsync(cancellationToken);
        var suggestionsTask = _httpClient.GetFromJsonAsync<List<ActionSuggestionViewModel>>(
            "api/suggestions", JsonOptions, cancellationToken);
        await Task.WhenAll(streamTask, historyTask, sensorsTask, suggestionsTask);
        var result = await streamTask
            ?? throw new InvalidOperationException("The command stream response was empty.");
        result.Filters = filters;
        result.Suggestions = await suggestionsTask ?? [];
        result.CommandHistory = await historyTask ?? [];
        result.DeviceNames = (await sensorsTask).ToDictionary(sensor => sensor.Id, sensor => sensor.DeviceIdentifier);
        return result;
    }

    public Task<CommandFeedback> ExecuteCommandAsync(DeviceCommandForm command, CancellationToken cancellationToken)
        => SendCommandAsync("api/commands", command, cancellationToken);

    public Task<CommandFeedback> UndoCommandAsync(UndoCommandForm command, CancellationToken cancellationToken)
        => SendCommandAsync("api/commands/undo", command, cancellationToken);

    private async Task<CommandFeedback> SendCommandAsync<T>(string route, T command, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(route, command, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or HttpStatusCode.Conflict))
        {
            response.EnsureSuccessStatusCode();
        }

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var message = body.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
        return new CommandFeedback(response.IsSuccessStatusCode,
            message ?? "The command could not be completed. Refresh and check the device state.");
    }

    public async Task SearchCommandStreamAsync(CommandStreamFilter filters, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/command-stream/search", filters, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public SmartXApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SensorViewModel>>
        GetSensorsAsync(
            CancellationToken cancellationToken = default)
    {
        return await _httpClient
            .GetFromJsonAsync<List<SensorViewModel>>(
                "api/sensors",
                JsonOptions,
                cancellationToken)
            ?? [];
    }

    public async Task<SensorViewModel?> GetSensorAsync(
        Guid sensorId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sensors/{sensorId}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(
            response,
            cancellationToken);

        return await response.Content
            .ReadFromJsonAsync<SensorViewModel>(
                JsonOptions,
                cancellationToken);
    }

    public async Task RegisterSensorAsync(
        RegisterSensorViewModel model,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/sensors",
            model,
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            cancellationToken);
    }

    public Task SendEnvironmentalAsync(
        Guid sensorId,
        float value,
        CancellationToken cancellationToken = default)
    {
        return SendTelemetryAsync(
            "api/telemetry/environmental",
            sensorId,
            value,
            "°C",
            cancellationToken);
    }

    public Task SendPowerAsync(
        Guid sensorId,
        int value,
        CancellationToken cancellationToken = default)
    {
        return SendTelemetryAsync(
            "api/telemetry/power",
            sensorId,
            value,
            "W",
            cancellationToken);
    }

    public Task SendActuatorAsync(
        Guid sensorId,
        bool value,
        CancellationToken cancellationToken = default)
    {
        return SendTelemetryAsync(
            "api/telemetry/actuator",
            sensorId,
            value,
            "state",
            cancellationToken);
    }

    public async Task<DashboardViewModel> GetDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        return await _httpClient
            .GetFromJsonAsync<DashboardViewModel>(
                "api/dashboard",
                JsonOptions,
                cancellationToken)
            ?? new DashboardViewModel();
    }

    public async Task<SimulationStatusViewModel> GetSimulationStatusAsync(CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<SimulationStatusViewModel>(
            "api/simulation/status", cancellationToken)
            ?? throw new InvalidOperationException("Simulation status is unavailable.");
    }

    public async Task StartSimulationAsync(int readingsPerSecond, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/simulation/start", new { readingsPerSecond }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task StopSimulationAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync("api/simulation/stop", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SeedAsync(
        int packetsPerSensor = 250,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/simulation/seed?packetsPerSensor={packetsPerSensor}",
            null,
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AttachmentViewModel>>
        GetAttachmentsAsync(
            Guid sensorId,
            CancellationToken cancellationToken = default)
    {
        return await _httpClient
            .GetFromJsonAsync<List<AttachmentViewModel>>(
                $"api/attachments/sensor/{sensorId}",
                JsonOptions,
                cancellationToken)
            ?? [];
    }

    public async Task UploadAttachmentAsync(
        Guid sensorId,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            throw new ArgumentException("Choose a non-empty file.");
        }

        if (file.Length > 10 * 1024 * 1024)
        {
            throw new ArgumentException("Files may not exceed 10 MB.");
        }

        string[] allowedExtensions = [".jpg", ".jpeg", ".png", ".txt", ".log", ".json", ".cfg"];
        if (!allowedExtensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("This file type is not permitted.");
        }

        using var content = new MultipartFormDataContent();
        await using var stream = file.OpenReadStream();

        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType);

        content.Add(
            fileContent,
            "file",
            file.FileName);

        using var response = await _httpClient.PostAsync(
            $"api/attachments/sensor/{sensorId}",
            content,
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            cancellationToken);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)>
        DownloadAttachmentAsync(
            Guid attachmentId,
            CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/attachments/{attachmentId}/download",
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            cancellationToken);

        var bytes = await response.Content.ReadAsByteArrayAsync(
            cancellationToken);

        var contentType =
            response.Content.Headers.ContentType?.MediaType
            ?? "application/octet-stream";

        var fileName =
            response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "attachment";

        return (
            bytes,
            contentType,
            fileName.Trim('"'));
    }

    private async Task SendTelemetryAsync<T>(
        string route,
        Guid sensorId,
        T value,
        string unit,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            sensorId,
            value,
            timestamp = DateTimeOffset.UtcNow,
            unit
        };

        using var response = await _httpClient.PostAsJsonAsync(
            route,
            payload,
            cancellationToken);

        await EnsureSuccessAsync(
            response,
            cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if ((int)response.StatusCode >= 500)
        {
            response.EnsureSuccessStatusCode();
        }

        var message = response.StatusCode == HttpStatusCode.RequestEntityTooLarge
            ? "Files may not exceed 10 MB."
            : "The API could not accept this request. Check your input and try again.";

        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var messages = errors.EnumerateObject()
                        .Where(error => error.Value.ValueKind == JsonValueKind.Array)
                        .SelectMany(error => error.Value.EnumerateArray())
                        .Where(error => error.ValueKind == JsonValueKind.String)
                        .Select(error => error.GetString())
                        .Where(error => !string.IsNullOrWhiteSpace(error))
                        .Distinct();
                    var validationMessage = string.Join(" ", messages);
                    if (!string.IsNullOrWhiteSpace(validationMessage))
                    {
                        message = validationMessage;
                    }
                }
                else
                {
                    foreach (var property in new[] { "message", "detail", "title" })
                    {
                        if (root.TryGetProperty(property, out var value) &&
                            value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                        {
                            message = value.GetString()!;
                            break;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException(message);
    }
}
