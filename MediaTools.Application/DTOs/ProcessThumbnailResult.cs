namespace MediaTools.Application.DTOs;

public sealed record ProcessThumbnailResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
