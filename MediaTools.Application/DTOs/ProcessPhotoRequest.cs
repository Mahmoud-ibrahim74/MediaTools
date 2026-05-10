using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record ProcessPhotoRequest(string SourcePath, string OutputFilePath, PhotoEnhanceSettings Settings);
