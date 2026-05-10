namespace MediaTools.Domain.Events;

public sealed record CompressionCompletedDomainEvent(Guid JobId, long OutputSizeBytes);
