namespace MediaTools.Application.DTOs;

/// <summary>Metadata fetched from a YouTube video before download.</summary>
public sealed record YouTubeVideoInfo(
    string Title,
    string ChannelName,
    TimeSpan Duration,
    string? ThumbnailUrl,
    long ViewCount);
