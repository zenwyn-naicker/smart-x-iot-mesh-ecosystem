using System.ComponentModel.DataAnnotations;

namespace SmartX.Api.Models;

public sealed record DeviceCommandRequest(Guid RequestId, Guid SensorId, [Required] bool? IsOn);

public sealed record UndoCommandRequest(Guid RequestId);
