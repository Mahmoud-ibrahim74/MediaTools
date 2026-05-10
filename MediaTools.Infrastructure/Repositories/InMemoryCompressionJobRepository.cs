using System.Collections.Concurrent;
using MediaTools.Application.Abstractions;
using MediaTools.Domain.Entities;

namespace MediaTools.Infrastructure.Repositories;

public sealed class InMemoryCompressionJobRepository : ICompressionJobRepository
{
    private readonly ConcurrentDictionary<Guid, CompressionJob> _jobs = new();

    public IReadOnlyList<CompressionJob> GetAll() =>
        [.. _jobs.Values.OrderByDescending(j => j.StartedAt ?? DateTimeOffset.MinValue)];

    public CompressionJob? GetById(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    public void Add(CompressionJob job) =>
        _jobs[job.Id] = job;

    public void Update(CompressionJob job) =>
        _jobs[job.Id] = job;
}
