using SmartX.Api.Models;
using SmartX.Api.Repositories;
using SmartX.Api.Services;

namespace SmartX.Api.Tests;

public sealed class SensorServiceTests
{
    [Fact]
    public void RecursiveValidatorAcceptsNestedDeployment()
    {
        var root = new DeploymentNode
        {
            Name = "Facility A",
            Level = DeploymentLevel.Facility,
            Children =
            [
                new DeploymentNode
                {
                    Name = "Zone 1",
                    Level = DeploymentLevel.Zone,
                    Children =
                    [
                        new DeploymentNode
                        {
                            Name = "Sub-Zone B",
                            Level = DeploymentLevel.SubZone,
                            Children =
                            [
                                new DeploymentNode
                                {
                                    Name = "Node 7",
                                    Level = DeploymentLevel.Node
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var result =
            new DeploymentTreeValidator().Validate(root);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RecursiveValidatorRejectsInvalidChildLevel()
    {
        var root = new DeploymentNode
        {
            Name = "Facility A",
            Level = DeploymentLevel.Facility,
            Children =
            [
                new DeploymentNode
                {
                    Name = "Node 7",
                    Level = DeploymentLevel.Node
                }
            ]
        };

        var result =
            new DeploymentTreeValidator().Validate(root);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void DuplicateSensorIdentifiersAreRejected()
    {
        var repository =
            new InMemorySensorRepository();

        var service = new SensorService(
            repository,
            new DeploymentTreeValidator(),
            new DeviceCommandService(repository, new UserActivityService(repository, new ActiveIssueService())));

        var request = new RegisterSensorRequest
        {
            DeviceIdentifier = "ESP32-ENV-01",
            Facility = "Facility A",
            Zone = "Zone 1",
            Node = "Node 7",
            Category = SensorCategory.Environmental
        };

        service.Register(request);

        Assert.Throws<InvalidOperationException>(
            () => service.Register(request));
    }
}
