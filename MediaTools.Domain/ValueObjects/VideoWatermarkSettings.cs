using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record VideoWatermarkSettings(
    WatermarkSourceKind Source,
    string? ImagePath,
    string? Text,
    WatermarkPosition Position,
    int OpacityPercent,
    int SizePercent);
