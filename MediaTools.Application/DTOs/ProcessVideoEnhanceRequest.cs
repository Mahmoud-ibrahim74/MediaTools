using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record ProcessVideoEnhanceRequest(
    string SourcePath,
    string OutputFilePath,
    VideoEnhancePipelineSettings Pipeline);
