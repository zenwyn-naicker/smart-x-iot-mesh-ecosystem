using SmartX.Api.Models;
using SmartX.Api.Repositories;

namespace SmartX.Api.Services;

public sealed class SensorService
{
    private readonly ISensorRepository _repository;
    private readonly DeploymentTreeValidator _validator;
    private readonly DeviceCommandService _commandService;

    public SensorService(
        ISensorRepository repository,
        DeploymentTreeValidator validator,
        DeviceCommandService commandService)
    {
        _repository = repository;
        _validator = validator;
        _commandService = commandService;
    }

    public SensorResponse Register(RegisterSensorRequest request)
    {
        if (!Enum.IsDefined(request.Category))
        {
            throw new ArgumentException("Select a valid sensor category.");
        }

        var root = BuildDeploymentTree(request);
        var validation = _validator.Validate(root);

        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Errors));
        }

        var sensor = new Sensor
        {
            DeviceIdentifier = request.DeviceIdentifier.Trim(),
            Facility = request.Facility.Trim(),
            Zone = request.Zone.Trim(),
            SubZone = string.IsNullOrWhiteSpace(request.SubZone)
                ? null
                : request.SubZone.Trim(),
            Node = request.Node.Trim(),
            Category = request.Category
        };

        if (!_repository.Add(sensor))
        {
            throw new InvalidOperationException(
                "A sensor with this identifier already exists.");
        }

        return SensorResponse.FromSensor(sensor);
    }

    public IReadOnlyList<SensorResponse> GetAll()
    {
        return _repository
            .GetAll()
            .Select(sensor => SensorResponse.FromSensor(sensor) with
            {
                ActuatorState = _commandService.GetState(sensor.Id)
            })
            .OrderBy(sensor => sensor.DeviceIdentifier)
            .ToList();
    }

    public SensorResponse? GetById(Guid id)
    {
        var sensor = _repository.GetById(id);

        return sensor is null
            ? null
            : SensorResponse.FromSensor(sensor) with
            {
                ActuatorState = _commandService.GetState(sensor.Id)
            };
    }

    private static DeploymentNode BuildDeploymentTree(
        RegisterSensorRequest request)
    {
        var node = new DeploymentNode
        {
            Name = request.Node,
            Level = DeploymentLevel.Node
        };

        DeploymentNode zoneChild = node;

        if (!string.IsNullOrWhiteSpace(request.SubZone))
        {
            zoneChild = new DeploymentNode
            {
                Name = request.SubZone,
                Level = DeploymentLevel.SubZone,
                Children = [node]
            };
        }

        var zone = new DeploymentNode
        {
            Name = request.Zone,
            Level = DeploymentLevel.Zone,
            Children = [zoneChild]
        };

        return new DeploymentNode
        {
            Name = request.Facility,
            Level = DeploymentLevel.Facility,
            Children = [zone]
        };
    }
}
