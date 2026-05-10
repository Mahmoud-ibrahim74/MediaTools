using MediaTools.Domain.Enums;

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
    string ResultMessage);
