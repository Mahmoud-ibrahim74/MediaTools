namespace MediaTools.Application.DTOs;

public sealed record ScreenRecordingResult(
    bool IsSuccess,
    bool IsCancelled,
    string? ErrorMessage,
    string? OutputFilePath,
    long? OutputFileSizeBytes,
    TimeSpan TotalDuration);
