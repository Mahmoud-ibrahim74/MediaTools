using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record StartScreenRecordingRequest(
    string OutputFilePath,
    ScreenRecordingSettings Settings);
