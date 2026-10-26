using Microsoft.Extensions.Logging.Abstractions;
using SmartX.Api.DataStructures;
using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class TelemetryProcessingWorkerTests
{
    [Fact]
    public async Task WorkerProcessesCriticalPacketsBeforeNormalPacketsAcrossTypes()
    {
        var repository = new InMemorySensorRepository();
        var environmental = AddSensor(repository, SensorCategory.Environmental);
        var power = AddSensor(repository, SensorCategory.PowerConsumption);
        var actuator = AddSensor(repository, SensorCategory.Actuator);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var classifier = new PowerTelemetryClassifier();
        var telemetry = new TelemetryService(repository, issues, commands, classifier);
        var queue = new TelemetryQueue(5, classifier);
        var timestamp = DateTimeOffset.UtcNow;
        telemetry.IngestPower(new(power, 1000, timestamp.AddSeconds(-1), "W"));
        queue.TryEnqueue(new TelemetryWorkItem.Environmental(
            new(environmental, 22f, timestamp, "C")));
        queue.TryEnqueue(new TelemetryWorkItem.Actuator(
            new(actuator, true, timestamp, "state")));
        queue.TryEnqueue(new TelemetryWorkItem.Power(
            new(power, 3000, timestamp, "W")));
        queue.TryEnqueue(new TelemetryWorkItem.Power(
            new(power, 1500, timestamp.AddSeconds(1), "W")));
        queue.TryEnqueue(new TelemetryWorkItem.Power(
            new(power, 3100, timestamp.AddSeconds(2), "W")));
        using var worker = CreateWorker(queue, telemetry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Equal(5, worker.GetStatus().Queued);
        await worker.StartAsync(timeout.Token);

        try
        {
            await WaitForCompletion(worker, 5, timeout.Token);
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(
                new long[] { 3, 5, 1, 2, 4 },
                worker.GetRecentProcessing().Select(item => item.ArrivalOrder));
            Assert.Equal(new[] { true, true, false, false, false },
                worker.GetRecentProcessing().Select(item => item.Critical));
            Assert.All(worker.GetRecentProcessing(), item => Assert.True(item.Succeeded));
            var history = telemetry.GetHistory(power);
            Assert.Equal(new[] { false, true, false, true }, history.Select(reading => reading.IsAnomaly));
            Assert.Equal(3100, telemetry.GetAggregatePower().Watts);
            Assert.Equal(new TelemetryProcessingStatus(0, 5, 5, 0), worker.GetStatus());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FailedPacketDoesNotPreventLaterPacketsFromProcessing()
    {
        var repository = new InMemorySensorRepository();
        var sensor = AddSensor(repository, SensorCategory.Environmental);
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var telemetry = new TelemetryService(repository, issues, commands);
        var queue = new TelemetryQueue(2);
        var timestamp = DateTimeOffset.UtcNow;
        queue.TryEnqueue(new TelemetryWorkItem.Power(
            new(Guid.NewGuid(), 4400, timestamp, "W")));
        queue.TryEnqueue(new TelemetryWorkItem.Environmental(
            new(sensor, 22f, timestamp, "C")));
        using var worker = CreateWorker(queue, telemetry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(timeout.Token);

        try
        {
            await WaitForCompletion(worker, 2, timeout.Token);

            Assert.Equal(sensor, Assert.Single(telemetry.GetHistory()).SensorId);
            Assert.Equal(new TelemetryProcessingStatus(0, 2, 1, 1), worker.GetStatus());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task IdleWorkerStopsWithoutRecordingAFailure()
    {
        var queue = new TelemetryQueue(1);
        var repository = new InMemorySensorRepository();
        var issues = new ActiveIssueService();
        var commands = new DeviceCommandService(repository, new UserActivityService(repository, issues));
        var telemetry = new TelemetryService(repository, issues, commands);
        using var worker = CreateWorker(queue, telemetry);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(timeout.Token);
        await worker.StopAsync(timeout.Token);

        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.False(worker.ExecuteTask.IsFaulted);
        Assert.Equal(new TelemetryProcessingStatus(0, 1, 0, 0), worker.GetStatus());
    }

    private static TelemetryProcessingWorker CreateWorker(
        TelemetryQueue queue,
        TelemetryService telemetry)
    {
        return new TelemetryProcessingWorker(
            queue, telemetry, NullLogger<TelemetryProcessingWorker>.Instance);
    }

    private static async Task WaitForCompletion(
        TelemetryProcessingWorker worker,
        int expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = worker.GetStatus();

            if (status.Processed + status.Failed == expected)
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static Guid AddSensor(InMemorySensorRepository repository, SensorCategory category)
    {
        var sensor = new Sensor
        {
            DeviceIdentifier = category.ToString(),
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 1",
            Category = category
        };
        repository.Add(sensor);
        return sensor.Id;
    }
}
