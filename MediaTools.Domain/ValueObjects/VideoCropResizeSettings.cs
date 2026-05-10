namespace MediaTools.Domain.ValueObjects;

public sealed record VideoCropResizeSettings(
    bool CropEnabled,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    bool ResizeEnabled,
    int? ResizeWidth,
    int? ResizeHeight);
