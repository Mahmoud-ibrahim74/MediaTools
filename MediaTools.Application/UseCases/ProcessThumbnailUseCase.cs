using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class ProcessThumbnailUseCase(IThumbnailGeneratorService thumbnailService)
{
    public async Task<ProcessThumbnailResult> ExecuteAsync(
        ProcessThumbnailRequest request,
        IProgress<ThumbnailProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await thumbnailService
                .GenerateAsync(
                    request.SourcePath,
                    request.OutputFilePath,
                    request.Settings,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ProcessThumbnailResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessThumbnailResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ProcessThumbnailResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
