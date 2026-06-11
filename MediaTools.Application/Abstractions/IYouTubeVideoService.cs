using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

/// <summary>
/// Downloads video from YouTube videos using yt-dlp + FFmpeg.
/// </summary>
public interface IYouTubeVideoService
{
    /// <summary>Ensures yt-dlp is downloaded and ready.</summary>
    Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches video metadata (title, thumbnail, duration, etc.) without downloading.</summary>
    Task<YouTubeVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Downloads the video track and merges it with audio into the requested format.</summary>
    Task<string> DownloadVideoAsync(
        YouTubeVideoDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        CancellationToken cancellationToken = default);
}
