using MediaTools.Domain.Entities;

namespace MediaTools.Application.Abstractions;

public interface ICompressionJobRepository
{
    IReadOnlyList<CompressionJob> GetAll();

    CompressionJob? GetById(Guid id);

    void Add(CompressionJob job);

    void Update(CompressionJob job);
}
