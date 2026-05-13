using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record AudioEnhanceSettings(
    AudioExportFormat TargetFormat,
    int BitrateKbps,
    AudioSampleRateOption SampleRate,
    bool NormalizeLoudness,
    int VolumePercent,
    bool ClarityBoost,
    bool IncludeVocalRemover,
    bool IncludeNoiseReduction,
    bool IncludeSilenceRemover,
    /// <summary>0 = mostly mid (original mono sum), 1 = mostly side (L−R) — requires stereo when vocal included.</summary>
    float VocalRemoverStrength01,
    /// <summary>Maps to FFmpeg afftdn strength.</summary>
    float NoiseReductionStrength01,
    /// <summary>dB threshold for silenceremove (e.g. −45).</summary>
    float SilenceThresholdDb,
    /// <summary>Minimum contiguous silence duration to trim (seconds).</summary>
    float MinSilenceDurationSec,
    /// <summary>Detection window tuning for silenceremove (seconds).</summary>
    float SilenceDetectionWindowSec);
