using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartX.Web.Controllers;
using SmartX.Web.Models;
using SmartX.Web.Services;

namespace SmartX.Api.Tests;

public sealed class WebFailureTests
{
    [Fact]
    public async Task SensorHistorySendsUtcRangeAndPreservesFiltersOnFailure()
    {
        var id = Guid.NewGuid();
        var filters = new SensorHistoryFilter
        {
            From = new DateTime(2026, 10, 11, 10, 0, 0),
            To = new DateTime(2026, 10, 11, 11, 0, 0),
            Limit = 10
        };
        var reject = false;
        var rangeCalls = 0;
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/api/sensors/{id}")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SensorViewModel { Id = id, DeviceIdentifier = "ENV-01" })
                };
            }
            rangeCalls++;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("2026-10-11T10:00:00.0000000Z", query["from"].ToString());
            Assert.Equal("2026-10-11T11:00:00.0000000Z", query["to"].ToString());
            Assert.Equal("10", query["limit"].ToString());
            Assert.Equal(id.ToString(), query["sensorId"].ToString());
            if (reject)
            {
                throw new HttpRequestException("Connection refused.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SensorHistoryRangeViewModel([], true))
            };
        });
        var controller = new SensorsController(new SmartXApiClient(http));
        var view = Assert.IsType<ViewResult>(await controller.History(id, filters, default));
        Assert.True(Assert.IsType<SensorHistoryViewModel>(view.Model).History!.HasMore);
        reject = true;
        var failed = Assert.IsType<ViewResult>(await controller.History(id, filters, default));
        Assert.Same(filters, Assert.IsType<SensorHistoryViewModel>(failed.Model).Filters);
        Assert.False(controller.ModelState.IsValid);
        controller.ModelState.Clear();
        filters.From = filters.To.Value.AddHours(1);
        await controller.History(id, filters, default);
        Assert.False(controller.ModelState.IsValid);
        Assert.Equal(2, rangeCalls);
    }

    [Fact]
    public async Task TopologySearchPrefersExactMatchesAndOnlyRequestsRoutesForSingleMatches()
    {
        var gateway = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var routeRequests = 0;
        var graph = new TopologyViewModel
        {
            Nodes = new Dictionary<Guid, MeshNodeViewModel>
            {
                [gateway] = new(gateway, "Smart-X Gateway", true, true, null),
                [first] = new(first, "ENV-01", false, true, null),
                [second] = new(second, "ENV-010", false, true, null)
            }
        };
        using var http = CreateHttpClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/topology")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(graph) };
            }
            if (request.RequestUri.AbsolutePath == "/api/simulation/status")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SimulationStatusViewModel(true, 1, null)
                    {
                        PausedSensorIds = [second]
                    })
                };
            }
            if (request.RequestUri.AbsolutePath == "/api/topology/reachability")
            {
                Assert.Equal($"?startNodeId={gateway}", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new MeshReachabilityViewModel(gateway, [gateway, first], [second]))
                };
            }
            Assert.Equal($"?startNodeId={first}", request.RequestUri.Query);
            routeRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MeshRouteViewModel(first, gateway, [first, gateway], 2, true))
            };
        });
        var client = new SmartXApiClient(http);
        var exact = await client.GetTopologyAsync(default, " env-01 ");
        Assert.Equal(first, Assert.Single(exact.Matches).Id);
        Assert.Equal("env-01", exact.Search);
        Assert.Equal(new[] { first, gateway }, exact.Route!.NodeIds);
        var partial = await client.GetTopologyAsync(default, "ENV");
        Assert.Equal(2, partial.Matches.Count);
        Assert.Null(partial.Route);
        var unknown = await client.GetTopologyAsync(default, "missing");
        Assert.Empty(unknown.Matches);
        Assert.Null(unknown.Route);
        var cleared = await client.GetTopologyAsync(default, " ");
        Assert.Empty(cleared.Matches);
        Assert.Null(cleared.Route);
        Assert.Equal(1, routeRequests);
        var controller = new TopologyController(client);
        var snapshot = Assert.IsType<PartialViewResult>(await controller.Index(default, " env-01 ", snapshot: true));
        Assert.Equal("Index", snapshot.ViewName);
        var model = Assert.IsType<TopologyViewModel>(snapshot.Model);
        Assert.Equal("env-01", model.Search);
        Assert.Equal(first, Assert.Single(model.Matches).Id);
        Assert.Equal(new[] { first, gateway }, model.Route!.NodeIds);
        Assert.True(model.Simulation!.IsRunning);
        Assert.Equal(second, Assert.Single(model.Simulation.PausedSensorIds));
        Assert.Equal(new[] { gateway, first }, model.Reachability!.ReachableNodeIds);
        Assert.Equal(second, Assert.Single(model.Reachability.UnreachableNodeIds));
        Assert.Equal(2, routeRequests);
    }

    [Theory]
    [InlineData("Sensors")]
    [InlineData("Details")]
    [InlineData("Download")]
    [InlineData("Dashboard")]
    [InlineData("Telemetry")]
    [InlineData("Topology")]
    public async Task UnavailableApiKeepsReadPagesUsable(string page)
    {
        using var http = CreateHttpClient(_ => throw new HttpRequestException("Connection refused."));
        var api = new SmartXApiClient(http);
        var sensors = new SensorsController(api);
        var result = page switch
        {
            "Sensors" => await sensors.Index(default),
            "Details" => await sensors.Details(Guid.NewGuid(), default),
            "Download" => await sensors.Download(Guid.NewGuid(), default),
            "Dashboard" => await new DashboardController(api).Index(default),
            "Topology" => await new TopologyController(api).Index(default),
            _ => await new TelemetryController(api).Index(default)
        };

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("ApiUnavailable", view.ViewName);
        Assert.Contains("Check the API connection", Assert.IsType<string>(view.Model));
        if (page == "Download")
        {
            var preview = Assert.IsType<ViewResult>(await sensors.Preview(Guid.NewGuid(), Guid.NewGuid(), default));
            Assert.Equal("ApiUnavailable", preview.ViewName);
        }
    }

    [Fact]
    public async Task RegistrationTimeoutPreservesEnteredValues()
    {
        using var http = CreateHttpClient(_ => throw new TaskCanceledException("Request timed out."));
        var controller = new SensorsController(new SmartXApiClient(http));
        var model = new RegisterSensorViewModel
        {
            DeviceIdentifier = "ENV-001",
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1"
        };

        var view = Assert.IsType<ViewResult>(await controller.Create(model, default));

        Assert.Same(model, view.Model);
        Assert.Contains("Registration could not be confirmed", controller.ModelState[""]!.Errors[0].ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TelemetryFailurePreservesTheSelectedSensorAndValue(bool failDuringSubmission)
    {
        var sensorId = Guid.NewGuid();
        using var http = CreateHttpClient(request =>
        {
            if (failDuringSubmission && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[] { new SensorViewModel { Id = sensorId } })
                };
            }
            throw new HttpRequestException("Connection refused.");
        });
        var controller = new TelemetryController(new SmartXApiClient(http));
        var model = new TelemetryFormViewModel { SensorId = sensorId, Value = "42.5" };

        var view = Assert.IsType<ViewResult>(await controller.Index(model, default));

        Assert.Same(model, view.Model);
        Assert.Equal(sensorId, model.SensorId);
        Assert.Equal("42.5", model.Value);
        Assert.Equal(!failDuringSubmission, model.SensorsUnavailable);
        Assert.False(controller.ModelState.IsValid);
    }

    [Fact]
    public async Task CancelledNavigationIsNotReportedAsAnApiOutage()
    {
        using var http = CreateHttpClient(_ => throw new InvalidOperationException("Should not send."));
        var controller = new DashboardController(new SmartXApiClient(http));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Index(cancellation.Token));
    }

    [Fact]
    public async Task FailedPollingReturnsServiceUnavailable()
    {
        using var http = CreateHttpClient(_ => throw new HttpRequestException("Connection refused."));
        var controller = new DashboardController(new SmartXApiClient(http));

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.Data(default)).StatusCode);
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await controller.SimulationStatus(default)).StatusCode);
        var topology = new TopologyController(new SmartXApiClient(http));
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await topology.Index(default, snapshot: true)).StatusCode);
    }

    [Theory]
    [InlineData(null, 0, "Choose a non-empty file.")]
    [InlineData("empty.txt", 0, "Choose a non-empty file.")]
    [InlineData("large.png", 10485761, "Files may not exceed 10 MB.")]
    [InlineData("program.exe", 1, "This file type is not permitted.")]
    public async Task InvalidAttachmentsAreRejectedBeforeAnApiRequest(string? name, long length, string expected)
    {
        using var http = CreateHttpClient(_ => throw new InvalidOperationException("Should not send."));
        var api = new SmartXApiClient(http);
        var file = name is null ? null : new FormFile(Stream.Null, 0, length, "file", name);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => api.UploadAttachmentAsync(Guid.NewGuid(), file));

        Assert.Equal(expected, error.Message);
    }

    [Theory]
    [InlineData("{\"message\":\"A sensor with this identifier already exists.\"}",
        "A sensor with this identifier already exists.")]
    [InlineData("{\"errors\":{\"Category\":[\"Select a valid sensor category.\"]}}",
        "Select a valid sensor category.")]
    [InlineData("<html>Internal proxy information</html>",
        "The API could not accept this request. Check your input and try again.")]
    public async Task ApiRejectionsProduceReadableMessages(string body, string expected)
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var api = new SmartXApiClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.RegisterSensorAsync(new RegisterSensorViewModel()));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task ServerFailureDoesNotExposeItsResponseBody()
    {
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"detail\":\"Private server information\"}")
        });
        var controller = new SensorsController(new SmartXApiClient(http));

        await controller.Create(new RegisterSensorViewModel(), default);

        var message = controller.ModelState[""]!.Errors[0].ErrorMessage;
        Assert.Contains("Registration could not be confirmed", message);
        Assert.DoesNotContain("Private", message);
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        return new HttpClient(new StubHandler(send)) { BaseAddress = new Uri("http://localhost") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }
}
