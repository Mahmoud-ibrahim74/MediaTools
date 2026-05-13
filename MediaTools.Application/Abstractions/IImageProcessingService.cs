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

    Task<byte[]?> GetBackgroundRemovalPreviewPngAsync(
        string sourcePath,
        BackgroundRemovalSettings settings,
        CancellationToken cancellationToken = default);

    Task RemoveBackgroundToFileAsync(
        string sourcePath,
        string outputPath,
        BackgroundRemovalSettings settings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetObjectEraserPreviewPngAsync(
        string sourcePath,
        IReadOnlyList<EraserBrushStamp> stamps,
        ObjectEraserSettings settings,
        CancellationToken cancellationToken = default);

    Task ApplyObjectEraserToFileAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<EraserBrushStamp> stamps,
        ObjectEraserSettings eraserSettings,
        PhotoEnhanceSettings encodeSettings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default);
}
