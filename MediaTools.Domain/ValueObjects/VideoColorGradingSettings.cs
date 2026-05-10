namespace MediaTools.Domain.ValueObjects;

public sealed record VideoColorGradingSettings(
    double Brightness,
    double Contrast,
    double Saturation,
    double Gamma,
    double Hue);
