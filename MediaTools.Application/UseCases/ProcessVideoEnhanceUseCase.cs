using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class ProcessVideoEnhanceUseCase(IVideoEnhanceService videoEnhanceService)
{
    public async Task<ProcessVideoEnhanceResult> ExecuteAsync(
        ProcessVideoEnhanceRequest request,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await videoEnhanceService
                .EnhanceAsync(
                    request.SourcePath,
                    request.OutputFilePath,
                    request.Settings,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ProcessVideoEnhanceResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessVideoEnhanceResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ProcessVideoEnhanceResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
