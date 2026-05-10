using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record ScreenRecordingSettings(
    ScreenRecordingRegion Region,
    int OffsetX,
    int OffsetY,
    int CaptureWidth,
    int CaptureHeight,
    int FrameRate,
    int Crf,
    bool CaptureCursor,
    bool IncludeMicrophone,
    string? MicrophoneDeviceName,
    ScreenRecordingOutputFormat OutputFormat,
    VideoHardwareEncoderKind VideoEncoder);
