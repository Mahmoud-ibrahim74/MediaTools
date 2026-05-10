using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record CompressionProfile(
    VideoCodec VideoCodec,
    AudioCodec AudioCodec,
    int Crf,
    EncodePreset EncodePreset,
    int? TargetWidth,
    int? TargetHeight,
    int AudioBitrateKbps,
    bool RemoveAudio,
    string OutputFileExtension)
{
    public static CompressionProfile HighQuality => new(
        VideoCodec.H265_HEVC,
        AudioCodec.AAC,
        Crf: 18,
        EncodePreset.Slow,
        TargetWidth: null,
        TargetHeight: null,
        AudioBitrateKbps: 192,
        RemoveAudio: false,
        OutputFileExtension: ".mp4");

    public static CompressionProfile Balanced => new(
        VideoCodec.H264,
        AudioCodec.AAC,
        Crf: 23,
        EncodePreset.Medium,
        TargetWidth: null,
        TargetHeight: null,
        AudioBitrateKbps: 160,
        RemoveAudio: false,
        OutputFileExtension: ".mp4");

    public static CompressionProfile SmallSize => new(
        VideoCodec.H265_HEVC,
        AudioCodec.AAC,
        Crf: 28,
        EncodePreset.Fast,
        TargetWidth: 1280,
        TargetHeight: 720,
        AudioBitrateKbps: 128,
        RemoveAudio: false,
        OutputFileExtension: ".mp4");

    public static CompressionProfile Web => new(
        VideoCodec.H264,
        AudioCodec.AAC,
        Crf: 26,
        EncodePreset.Fast,
        TargetWidth: null,
        TargetHeight: null,
        AudioBitrateKbps: 128,
        RemoveAudio: false,
        OutputFileExtension: ".mp4");
}
