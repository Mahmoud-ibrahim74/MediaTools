namespace MediaTools.Application.DTOs;

public sealed record ProcessPhotoResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
