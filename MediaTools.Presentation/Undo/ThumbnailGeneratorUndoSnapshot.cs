using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Undo;

public sealed record ThumbnailGeneratorUndoSnapshot(
    string? SelectedFilePath,
    string FileDisplayName,
    string FileSizeDisplay,
    string DurationDisplay,
    string DimensionsDisplay,
    string FormatDisplay,
    bool IsSourceVideo,
    double SourceDurationSeconds,
    int MaxEdgePixels,
    int JpegWebpQuality,
    double VideoTimeOffsetSeconds,
    ThumbnailOutputFormat OutputFormat,
    double ProgressPercent01,
    string ProgressStatusText,
    string ProgressDetailText,
    bool FinishedAttempt,
    bool Succeeded,
    string ResultMessage);
