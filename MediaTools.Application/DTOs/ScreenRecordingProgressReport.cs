namespace MediaTools.Application.DTOs;

public sealed record ScreenRecordingProgressReport(
    TimeSpan Elapsed,
    long? CurrentSizeBytes,
    string StepDescription);
