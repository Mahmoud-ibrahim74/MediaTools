using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Entities;
using MediaTools.Domain.Enums;

namespace MediaTools.Application.UseCases;

public sealed class CompressVideoUseCase(
    IVideoCompressionService videoCompressionService,
    ICompressionJobRepository jobRepository)
{
    public async Task<CompressVideoResult> ExecuteAsync(
        CompressVideoRequest request,
        IProgress<CompressionProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mediaFile = await videoCompressionService.AnalyzeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);

        var jobId = Guid.NewGuid();
        var job = new CompressionJob(jobId, mediaFile, request.OutputFilePath, request.Profile);

        jobRepository.Add(job);

        job.Start(DateTimeOffset.UtcNow);
        jobRepository.Update(job);

        var progressBridge = new Progress<CompressionProgressReport>(report =>
        {
            progress?.Report(report);
            jobRepository.Update(job);
        });

        try
        {
            await videoCompressionService
                .CompressAsync(job, progressBridge, cancellationToken)
                .ConfigureAwait(false);

            if (job.Status == CompressionJobStatus.Cancelled)
            {
                jobRepository.Update(job);
                return new CompressVideoResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
            }

            if (job.Status == CompressionJobStatus.Failed)
            {
                jobRepository.Update(job);
                return new CompressVideoResult(IsSuccess: false, IsCancelled: false, ErrorMessage: job.ErrorMessage);
            }

            jobRepository.Update(job);
            return new CompressVideoResult(IsSuccess: true, IsCancelled: false, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            job.Cancel(DateTimeOffset.UtcNow);
            jobRepository.Update(job);
            return new CompressVideoResult(IsSuccess: false, IsCancelled: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message, DateTimeOffset.UtcNow);
            jobRepository.Update(job);
            return new CompressVideoResult(IsSuccess: false, IsCancelled: false, ErrorMessage: ex.Message);
        }
    }
}
