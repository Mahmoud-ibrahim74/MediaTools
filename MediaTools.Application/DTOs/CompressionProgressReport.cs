namespace MediaTools.Application.DTOs;

public sealed record CompressionProgressReport(
    double Percent01,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    string CurrentStepDescription);
