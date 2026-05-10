using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class ProcessPhotoUseCase(IImageProcessingService imageProcessing)
{
    public async Task<ProcessPhotoResult> ExecuteAsync(
        ProcessPhotoRequest request,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await imageProcessing
                .ProcessAsync(request.SourcePath, request.OutputFilePath, request.Settings, progress, cancellationToken)
                .ConfigureAwait(false);

            return new ProcessPhotoResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessPhotoResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ProcessPhotoResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
