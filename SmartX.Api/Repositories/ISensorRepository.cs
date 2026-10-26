using SmartX.Api.Models;

namespace SmartX.Api.Repositories;

public interface ISensorRepository
{
    bool Add(Sensor sensor);

    Sensor? GetById(Guid id);

    Sensor? GetByIdentifier(string deviceIdentifier);

    IReadOnlyList<Sensor> GetAll();

    void UpdateHeartbeat(Guid id, DateTimeOffset timestamp);
}
