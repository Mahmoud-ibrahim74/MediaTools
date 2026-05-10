namespace MediaTools.Application.DTOs;

public sealed record ProcessVideoEnhanceResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
