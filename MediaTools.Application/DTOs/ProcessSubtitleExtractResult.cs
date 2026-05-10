namespace MediaTools.Application.DTOs;

public sealed record ProcessSubtitleExtractResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
