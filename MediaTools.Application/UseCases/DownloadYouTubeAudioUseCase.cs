using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class DownloadYouTubeAudioUseCase(IYouTubeAudioService youTubeAudio)
{
    public async Task<YouTubeDownloadResult> ExecuteAsync(
        YouTubeDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var outputPath = await youTubeAudio
                .DownloadAudioAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);

            return new YouTubeDownloadResult(
                IsSuccess: true,
                IsCancelled: false,
                ErrorMessage: null,
                OutputFilePath: outputPath);
        }
        catch (OperationCanceledException)
        {
            return new YouTubeDownloadResult(
                IsSuccess: false,
                IsCancelled: true,
                ErrorMessage: null,
                OutputFilePath: null);
        }
        catch (Exception ex)
        {
            return new YouTubeDownloadResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: ex.Message,
                OutputFilePath: null);
        }
    }
}
