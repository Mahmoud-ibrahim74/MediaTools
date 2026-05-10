using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record ProcessThumbnailRequest(
    string SourcePath,
    string OutputFilePath,
    ThumbnailGeneratorSettings Settings);
