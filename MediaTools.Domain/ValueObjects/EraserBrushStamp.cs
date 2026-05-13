namespace MediaTools.Domain.ValueObjects;

/// <summary>
/// One dab of the object eraser brush in **full image pixel** coordinates.
/// </summary>
public readonly record struct EraserBrushStamp(
    float ImagePixelX,
    float ImagePixelY,
    float RadiusPx,
    float Softness01);
