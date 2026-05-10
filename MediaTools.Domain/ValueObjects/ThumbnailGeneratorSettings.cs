using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record ThumbnailGeneratorSettings(
    int MaxEdgePixels,
    int JpegWebpQuality,
    double VideoTimeOffsetSeconds,
    ThumbnailOutputFormat OutputFormat);
