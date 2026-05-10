namespace MediaTools.Domain.Events;

public sealed record CompressionProgressDomainEvent(Guid JobId, double Progress01, string? StepDescription);
