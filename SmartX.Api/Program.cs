using Microsoft.AspNetCore.DataProtection;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartXWeb", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5018",
                "https://localhost:7045")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<ISensorRepository, InMemorySensorRepository>();
builder.Services.AddSingleton<IMeshRepository, InMemoryMeshRepository>();
builder.Services.AddSingleton<MeshTopologyService>();
builder.Services.AddSingleton<DeploymentTreeValidator>();
builder.Services.AddSingleton<SensorService>();
builder.Services.AddSingleton<DeviceCommandService>();
builder.Services.AddSingleton<UserActivityService>();
builder.Services.AddSingleton<ActionPatternService>();
builder.Services.AddSingleton<ActionSuggestionService>();
builder.Services.AddSingleton<ActiveIssueService>();
builder.Services.AddSingleton<PowerTelemetryClassifier>();
builder.Services.AddSingleton(provider => new TelemetryQueue(
    10000, provider.GetRequiredService<PowerTelemetryClassifier>()));
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<TelemetryProcessingWorker>();
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<TelemetryProcessingWorker>());
builder.Services.AddSingleton<DemoDataService>();
builder.Services.AddHttpClient("SimulatedDevices", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Simulation:ApiBaseUrl"] ?? "http://localhost:5082");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<SimulationWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SimulationWorker>());
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<CommandStreamService>();

var keyDirectory = Path.Combine(
    builder.Environment.ContentRootPath,
    "App_Data",
    "keys");

Directory.CreateDirectory(keyDirectory);

builder.Services
    .AddDataProtection()
    .SetApplicationName("SmartX")
    .PersistKeysToFileSystem(
        new DirectoryInfo(keyDirectory));

builder.Services.AddSingleton<AttachmentService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("SmartXWeb");
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program
{
}
