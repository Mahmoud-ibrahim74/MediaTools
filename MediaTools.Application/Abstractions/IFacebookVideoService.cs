using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

/// <summary>
/// Downloads video from Facebook using yt-dlp + FFmpeg for DASH stream merging.
/// </summary>
public interface IFacebookVideoService
{
    /// <summary>Ensures yt-dlp and FFmpeg are available.</summary>
    Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches video metadata (title, thumbnail, duration, etc.) without downloading.</summary>
    Task<FacebookVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Downloads the video track and merges it with audio into the requested format.</summary>
    Task<string> DownloadVideoAsync(
        FacebookVideoDownloadRequest request,
        IProgress<FacebookDownloadProgress> progress,
        CancellationToken cancellationToken = default);
}
