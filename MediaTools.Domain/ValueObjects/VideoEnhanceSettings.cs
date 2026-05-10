using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record VideoEnhanceSettings(
    VideoEnhanceOperation Operation,
    VideoWatermarkSettings? Watermark,
    VideoSpeedSettings? Speed,
    VideoCropResizeSettings? CropResize,
    VideoColorGradingSettings? ColorGrading,
    VideoStabilizerSettings? Stabilizer,
    VideoToAudioSettings? ToAudio);
