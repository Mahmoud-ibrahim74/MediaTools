using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Domain.Entities;

public sealed class CompressionJob
{
    public CompressionJob(Guid id, MediaFile sourceFile, string outputPath, CompressionProfile profile)
    {
        Id = id;
        SourceFile = sourceFile ?? throw new ArgumentNullException(nameof(sourceFile));
        OutputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public Guid Id { get; }
    public MediaFile SourceFile { get; }
    public string OutputPath { get; }
    public CompressionProfile Profile { get; }

    public CompressionJobStatus Status { get; private set; } = CompressionJobStatus.Pending;
    public double Progress { get; private set; }
    public long? OutputSizeBytes { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public double? CompressionRatio =>
        OutputSizeBytes is > 0 && SourceFile.FileSizeBytes > 0
            ? (double)OutputSizeBytes.Value / SourceFile.FileSizeBytes
            : null;

    public double? SpaceSavedRatio =>
        CompressionRatio is > 0 and < 1
            ? 1 - CompressionRatio.Value
            : null;

    public TimeSpan? ElapsedTime =>
        StartedAt is null
            ? null
            : (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;

    public void Start(DateTimeOffset startedAt)
    {
        Status = CompressionJobStatus.Running;
        StartedAt = startedAt;
        Progress = 0;
        ErrorMessage = null;
        OutputSizeBytes = null;
        CompletedAt = null;
    }

    public void UpdateProgress(double progress01)
    {
        Progress = Math.Clamp(progress01, 0, 1);
    }

    public void Complete(long outputSizeBytes, DateTimeOffset completedAt)
    {
        Status = CompressionJobStatus.Completed;
        Progress = 1;
        OutputSizeBytes = outputSizeBytes;
        CompletedAt = completedAt;
        ErrorMessage = null;
    }

    public void Fail(string errorMessage, DateTimeOffset completedAt)
    {
        Status = CompressionJobStatus.Failed;
        ErrorMessage = errorMessage;
        CompletedAt = completedAt;
    }

    public void Cancel(DateTimeOffset completedAt)
    {
        Status = CompressionJobStatus.Cancelled;
        CompletedAt = completedAt;
    }
}
