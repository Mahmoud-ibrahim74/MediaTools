using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Application.Abstractions;

public interface ISubtitleExtractorService
{
    Task<SubtitleSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task ExtractAsync(
        string sourcePath,
        string outputPath,
        int subtitleStreamIndex,
        SubtitleExportFormat exportFormat,
        IProgress<SubtitleExtractProgressReport> progress,
        CancellationToken cancellationToken = default);
}
