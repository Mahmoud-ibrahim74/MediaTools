using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Undo;

public sealed record VideoCompressUndoSnapshot(
    string? SelectedFilePath,
    string FileDisplayName,
    string FileSizeDisplay,
    string DurationDisplay,
    string FormatDisplay,
    long SourceSizeBytes,
    int Crf,
    VideoCodec SelectedVideoCodec,
    AudioCodec SelectedAudioCodec,
    EncodePreset SelectedEncodePreset,
    string? TargetWidthInput,
    string? TargetHeightInput,
    int AudioBitrateKbps,
    bool RemoveAudio,
    double ProgressPercent01,
    string ProgressStatusText,
    string ProgressDetailText,
    string ElapsedDisplay,
    string EstimatedRemainingDisplay,
    string ResultOriginalSizeDisplay,
    string ResultCompressedSizeDisplay,
    string SavedPercentDisplay,
    string ResultSummaryText,
    bool CompressionSucceeded,
    bool CompressionAttemptFinished,
    string SelectedProfileKey);
