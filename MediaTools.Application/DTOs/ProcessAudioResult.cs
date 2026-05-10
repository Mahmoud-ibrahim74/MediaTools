namespace MediaTools.Application.DTOs;

public sealed record ProcessAudioResult(bool IsSuccess, bool IsCancelled, string? ErrorMessage);
