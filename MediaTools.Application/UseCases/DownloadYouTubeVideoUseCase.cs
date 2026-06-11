using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class DownloadYouTubeVideoUseCase(IYouTubeVideoService youTubeVideo)
{
    public async Task<YouTubeDownloadResult> ExecuteAsync(
        YouTubeVideoDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var outputPath = await youTubeVideo
                .DownloadVideoAsync(request, progress, cancellationToken)
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
