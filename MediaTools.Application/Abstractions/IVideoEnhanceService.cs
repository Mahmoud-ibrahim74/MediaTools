using MediaTools.Application.DTOs;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.Abstractions;

public interface IVideoEnhanceService
{
    Task<VideoSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task EnhanceAsync(
        string sourcePath,
        string outputPath,
        VideoEnhanceSettings settings,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a single JPEG frame (for UI preview) for operations that map to a lightweight video filter.
    /// Returns null when preview is not supported or FFmpeg fails.
    /// </summary>
    Task<byte[]?> TryRenderEffectPreviewJpegAsync(
        string sourcePath,
        VideoEnhanceSettings settings,
        CancellationToken cancellationToken = default);
}
