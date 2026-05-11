using MediaTools.Domain.Entities;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

public interface IVideoCompressionService
{
    /// <summary>True after FFmpeg binaries are present and configured (may still fail individual operations).</summary>
    bool IsToolsReady { get; }

    /// <summary>True while FFmpeg is being downloaded or verified on first use.</summary>
    bool IsToolsPreparing { get; }

    /// <summary>Populated when the last <see cref="EnsureToolsReadyAsync"/> failed.</summary>
    string? ToolsPrepareError { get; }

    event EventHandler? ToolsAvailabilityChanged;

    Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default);

    Task<MediaFile> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task CompressAsync(
        CompressionJob job,
        IProgress<CompressionProgressReport> progress,
        CancellationToken cancellationToken = default);
}
