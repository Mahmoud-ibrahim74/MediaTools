namespace MediaTools.Application.DTOs;

public sealed record CompressVideoResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
