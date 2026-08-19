using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class DownloadFacebookVideoUseCase(IFacebookVideoService facebookVideo)
{
    public async Task<FacebookDownloadResult> ExecuteAsync(
        FacebookVideoDownloadRequest request,
        IProgress<FacebookDownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var outputPath = await facebookVideo
                .DownloadVideoAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);

            return new FacebookDownloadResult(
                IsSuccess: true,
                IsCancelled: false,
                ErrorMessage: null,
                OutputFilePath: outputPath);
        }
        catch (OperationCanceledException)
        {
            return new FacebookDownloadResult(
                IsSuccess: false,
                IsCancelled: true,
                ErrorMessage: null,
                OutputFilePath: null);
        }
        catch (Exception ex)
        {
            return new FacebookDownloadResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: ex.Message,
                OutputFilePath: null);
        }
    }
}
