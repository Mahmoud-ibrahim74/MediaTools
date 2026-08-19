using MediaTools.Domain.Enums;

namespace MediaTools.Application.DTOs;

/// <summary>Metadata extracted from a Facebook video without downloading.</summary>
public sealed record FacebookVideoInfo(
    string Title,
    string AuthorName,
    TimeSpan Duration,
    string? ThumbnailUrl,
    long ViewCount);

/// <summary>Request to download a Facebook video with format/quality options.</summary>
public sealed record FacebookVideoDownloadRequest(
    string Url,
    string OutputFolderPath,
    FacebookVideoFormat VideoFormat = FacebookVideoFormat.Mp4,
    string Resolution = "Best",
    string VideoQuality = "High");

/// <summary>Progress report emitted during a Facebook video download and stream merge.</summary>
public sealed record FacebookDownloadProgress(
    double ProgressPercent,
    string StatusText,
    string? DownloadSpeedDisplay,
    string? EtaDisplay,
    bool IsMuxing = false);

/// <summary>Result of a Facebook video download operation.</summary>
public sealed record FacebookDownloadResult(
    bool IsSuccess,
    bool IsCancelled,
    string? ErrorMessage,
    string? OutputFilePath);
