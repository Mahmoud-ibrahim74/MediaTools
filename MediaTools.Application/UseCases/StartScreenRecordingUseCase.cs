using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class StartScreenRecordingUseCase(IScreenRecordingService screenRecordingService)
{
    public async Task<ScreenRecordingResult> ExecuteAsync(
        StartScreenRecordingRequest request,
        IProgress<ScreenRecordingProgressReport> progress,
        CancellationToken stopSignal,
        CancellationToken cancellationToken = default,
        Action<IPausableRecordingControl>? onRecordingStarted = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await screenRecordingService
                .RecordAsync(
                    request.OutputFilePath,
                    request.Settings,
                    progress,
                    stopSignal,
                    cancellationToken,
                    onRecordingStarted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: true,
                ErrorMessage: null,
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: ex.Message,
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: TimeSpan.Zero);
        }
    }
}
