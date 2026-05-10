using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record AudioEnhanceSettings(
    AudioExportFormat TargetFormat,
    int BitrateKbps,
    AudioSampleRateOption SampleRate,
    bool NormalizeLoudness,
    int VolumePercent,
    bool ClarityBoost);
