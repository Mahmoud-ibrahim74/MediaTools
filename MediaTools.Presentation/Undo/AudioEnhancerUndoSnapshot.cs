using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Undo;

public sealed record AudioEnhancerUndoSnapshot(
    string? SelectedFilePath,
    string FileDisplayName,
    string FileSizeDisplay,
    string DurationDisplay,
    string CodecDisplay,
    string SampleRateDisplay,
    string ChannelsDisplay,
    int SourceAudioChannels,
    int WorkspaceTabIndex,
    bool IncludeVocalRemover,
    bool IncludeNoiseReduction,
    bool IncludeSilenceRemover,
    double VocalRemoverStrength,
    double NoiseReductionStrength,
    double SilenceThresholdDb,
    double MinSilenceDurationSec,
    double SilenceDetectionWindowSec,
    AudioExportFormat TargetFormat,
    int BitrateKbps,
    AudioSampleRateOption SampleRate,
    bool NormalizeLoudness,
    int VolumePercent,
    bool ClarityBoost,
    double ProgressPercent01,
    string ProgressStatusText,
    string ProgressDetailText,
    bool FinishedAttempt,
    bool Succeeded,
    string ResultMessage);
