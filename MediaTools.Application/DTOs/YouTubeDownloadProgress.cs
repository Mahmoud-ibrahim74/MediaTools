namespace MediaTools.Application.DTOs;

/// <summary>Progress report emitted during a YouTube audio download.</summary>
public sealed record YouTubeDownloadProgress(
    double ProgressPercent,
    string StatusText,
    string? DownloadSpeedDisplay,
    string? EtaDisplay);
