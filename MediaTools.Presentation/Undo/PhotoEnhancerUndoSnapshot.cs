using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Presentation.Undo;

public sealed record PhotoEnhancerUndoSnapshot(
    string? SelectedFilePath,
    string FileDisplayName,
    string FileSizeDisplay,
    string DimensionsDisplay,
    string FormatDisplay,
    RasterImageFormat TargetFormat,
    int EncodingQuality,
    PhotoResizeIntent ResizeIntent,
    double ScaleFactor,
    int SelectedMaxEdge,
    UpscaleQualityMode UpscaleQualityMode,
    PhotoFilterKind SelectedFilter,
    double ProgressPercent01,
    string ProgressStatusText,
    string ProgressDetailText,
    bool FinishedAttempt,
    bool Succeeded,
    string ResultMessage,
    PhotoEnhancerWorkspace Workspace,
    BackgroundRemovalMode BgMode,
    int BgTolerance,
    float BgFeatherSigma,
    byte BgKeyR,
    byte BgKeyG,
    byte BgKeyB,
    float BgLuminanceThreshold01,
    int BgEdgeExpandPx,
    EraserBrushStamp[] EraserStrokes,
    float EraserBlurSigma,
    float EraserBrushSoftness01,
    float EraserBrushRadiusPx);
