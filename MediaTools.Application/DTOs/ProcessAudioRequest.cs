using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record ProcessAudioRequest(
    string SourcePath,
    string OutputFilePath,
    AudioEnhanceSettings Settings);
