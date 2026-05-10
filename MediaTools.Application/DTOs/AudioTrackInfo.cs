namespace MediaTools.Application.DTOs;

public sealed record AudioTrackInfo(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    TimeSpan Duration,
    string Codec,
    int SampleRateHz,
    int Channels,
    int? BitrateKbps);
