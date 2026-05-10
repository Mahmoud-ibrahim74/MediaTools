namespace MediaTools.Application.DTOs;

public sealed record ThumbnailSourceAnalysis(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    bool IsVideo,
    TimeSpan? Duration,
    int? MediaWidth,
    int? MediaHeight,
    string FormatHint);
