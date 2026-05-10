using MediaTools.Application.DTOs;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.Abstractions;

public interface IThumbnailGeneratorService
{
    Task<ThumbnailSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task GenerateAsync(
        string sourcePath,
        string outputPath,
        ThumbnailGeneratorSettings settings,
        IProgress<ThumbnailProgressReport> progress,
        CancellationToken cancellationToken = default);
}
