using MediaTools.Domain.Entities;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

public interface IVideoCompressionService
{
    Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default);

    Task<MediaFile> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default);

    Task CompressAsync(
        CompressionJob job,
        IProgress<CompressionProgressReport> progress,
        CancellationToken cancellationToken = default);
}
