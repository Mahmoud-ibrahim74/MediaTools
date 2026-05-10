namespace MediaTools.Application.DTOs;

public sealed record SubtitleSourceAnalysis(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    TimeSpan? Duration,
    string ContainerFormatHint,
    IReadOnlyList<SubtitleTrackInfoDto> SubtitleTracks);
