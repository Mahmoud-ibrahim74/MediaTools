using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record PhotoEnhanceSettings(
    RasterImageFormat TargetFormat,
    int EncodingQuality,
    PhotoResizeIntent ResizeIntent,
    double ScaleFactor,
    int? MaxEdgePixels,
    UpscaleQualityMode UpscaleMode,
    PhotoFilterKind Filter);
