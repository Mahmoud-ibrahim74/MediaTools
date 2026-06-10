using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

/// <summary>
/// Downloads audio from YouTube videos using yt-dlp + FFmpeg.
/// The implementation bootstraps yt-dlp on first use, similar to FFmpeg auto-download.
/// </summary>
public interface IYouTubeAudioService
{
    /// <summary>Ensures yt-dlp is downloaded and ready.</summary>
    Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches video metadata (title, thumbnail, duration, etc.) without downloading.</summary>
    Task<YouTubeVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Downloads the audio track and converts it to the requested format.</summary>
    Task<string> DownloadAudioAsync(
        YouTubeDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        CancellationToken cancellationToken = default);
}
