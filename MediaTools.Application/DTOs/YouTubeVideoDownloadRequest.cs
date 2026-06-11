using MediaTools.Domain.Enums;

namespace MediaTools.Application.DTOs;

/// <summary>Request parameters for a YouTube video download.</summary>
public sealed record YouTubeVideoDownloadRequest(
    string Url,
    string OutputFolderPath,
    YouTubeVideoFormat VideoFormat,
    string Resolution,
    bool IsPlaylist = false);
