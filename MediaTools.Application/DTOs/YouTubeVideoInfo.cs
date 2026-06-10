using System.Collections.Generic;

namespace MediaTools.Application.DTOs;

/// <summary>Represents a single entry in a YouTube playlist.</summary>
public sealed record PlaylistItemInfo(
    string Title,
    string Url,
    string? ChannelName,
    TimeSpan Duration);

/// <summary>Metadata fetched from a YouTube video or playlist before download.</summary>
public sealed record YouTubeVideoInfo(
    string Title,
    string ChannelName,
    TimeSpan Duration,
    string? ThumbnailUrl,
    long ViewCount,
    bool IsPlaylist = false,
    int VideoCount = 0,
    IReadOnlyList<PlaylistItemInfo>? PlaylistItems = null);
