namespace MediaTools.Application.DTOs;

public sealed record VideoSourceAnalysis(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    TimeSpan Duration,
    int Width,
    int Height,
    string VideoCodec,
    bool HasAudio);
