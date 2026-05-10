using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

/// <summary>Ordered video processing steps sharing one hardware encoder choice. Steps must be video-only (no extract audio/subtitle).</summary>
public sealed record VideoEnhancePipelineSettings(
    VideoHardwareEncoderKind VideoEncoder,
    IReadOnlyList<VideoEnhanceSettings> Steps);
