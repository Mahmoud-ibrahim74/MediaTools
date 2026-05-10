namespace MediaTools.Domain.Events;

public sealed record CompressionFailedDomainEvent(Guid JobId, string ErrorMessage);
