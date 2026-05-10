using MediaTools.Application.DTOs;
using MediaTools.Domain.Entities;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.Abstractions;

public interface IImageProcessingService
{
    Task<RasterImageFile> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task ProcessAsync(
        string sourcePath,
        string outputPath,
        PhotoEnhanceSettings settings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders the same edit pipeline as <see cref="ProcessAsync"/> into an in-memory PNG for UI preview (downscaled for performance).
    /// </summary>
    Task<byte[]?> GetEditedPreviewPngAsync(
        string sourcePath,
        PhotoEnhanceSettings settings,
        CancellationToken cancellationToken = default);
}
