using MediaTools.Application.DTOs;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.Abstractions;

public interface IAudioProcessingService
{
    Task<AudioTrackInfo> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task ProcessAsync(
        string sourcePath,
        string outputPath,
        AudioEnhanceSettings settings,
        IProgress<AudioProgressReport> progress,
        CancellationToken cancellationToken = default);
}
