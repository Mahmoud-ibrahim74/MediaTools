using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Application.UseCases;

public sealed class ProcessSubtitleExtractUseCase(ISubtitleExtractorService subtitleExtractorService)
{
    public async Task<ProcessSubtitleExtractResult> ExecuteAsync(
        ProcessSubtitleExtractRequest request,
        IProgress<SubtitleExtractProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await subtitleExtractorService
                .ExtractAsync(
                    request.SourcePath,
                    request.OutputFilePath,
                    request.SubtitleStreamIndex,
                    request.ExportFormat,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ProcessSubtitleExtractResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessSubtitleExtractResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ProcessSubtitleExtractResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
