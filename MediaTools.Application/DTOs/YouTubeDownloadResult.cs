namespace MediaTools.Application.DTOs;

/// <summary>Result of a completed YouTube audio download operation.</summary>
public sealed record YouTubeDownloadResult(
    bool IsSuccess,
    bool IsCancelled,
    string? ErrorMessage,
    string? OutputFilePath);
