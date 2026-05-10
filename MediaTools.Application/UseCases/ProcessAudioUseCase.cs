using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class ProcessAudioUseCase(IAudioProcessingService audioProcessing)
{
    public async Task<ProcessAudioResult> ExecuteAsync(
        ProcessAudioRequest request,
        IProgress<AudioProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await audioProcessing
                .ProcessAsync(
                    request.SourcePath,
                    request.OutputFilePath,
                    request.Settings,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ProcessAudioResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessAudioResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ProcessAudioResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
