using MediaTools.Domain.Enums;

namespace MediaTools.Application.DTOs;

/// <summary>Request parameters for a YouTube audio download.</summary>
public sealed record YouTubeDownloadRequest(
    string Url,
    string OutputFolderPath,
    YouTubeAudioFormat AudioFormat,
    int BitrateKbps);
