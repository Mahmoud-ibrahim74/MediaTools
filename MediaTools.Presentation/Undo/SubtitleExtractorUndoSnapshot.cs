using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Undo;

public sealed record SubtitleExtractorUndoSnapshot(
    string? SelectedFilePath,
    string FileDisplayName,
    string FileSizeDisplay,
    string DurationDisplay,
    string FormatDisplay,
    SubtitleTrackInfoDto[] SubtitleTracks,
    int? SelectedStreamIndex,
    SubtitleExportFormat ExportFormat,
    double ProgressPercent01,
    string ProgressStatusText,
    string ProgressDetailText,
    bool FinishedAttempt,
    bool Succeeded,
    string ResultMessage);
