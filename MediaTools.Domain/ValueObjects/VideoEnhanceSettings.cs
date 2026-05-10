using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record VideoEnhanceSettings(
    VideoEnhanceOperation Operation,
    VideoHardwareEncoderKind VideoEncoder,
    VideoWatermarkSettings? Watermark,
    VideoSpeedSettings? Speed,
    VideoCropResizeSettings? CropResize,
    VideoColorGradingSettings? ColorGrading,
    VideoStabilizerSettings? Stabilizer,
    VideoToAudioSettings? ToAudio);
