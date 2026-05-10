using System.Globalization;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Events;

namespace MediaTools.Infrastructure.Services;

public sealed class FfmpegSubtitleExtractorService(IVideoCompressionService videoCompressionService) : ISubtitleExtractorService
{
    public async Task<SubtitleSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File was not found.", filePath);
        }

        var info = await FFmpeg.GetMediaInfo(filePath, cancellationToken).ConfigureAwait(false);
        var sizeBytes = info.Size > 0 ? info.Size : new FileInfo(filePath).Length;
        var duration = info.Duration > TimeSpan.Zero ? info.Duration : (TimeSpan?)null;

        var tracks = info.SubtitleStreams
            .Select(
                s => new SubtitleTrackInfoDto(
                    s.Index,
                    s.Codec,
                    s.Language ?? string.Empty,
                    s.Title ?? string.Empty))
            .OrderBy(t => t.StreamIndex)
            .ToList();

        var formatHint = info.VideoStreams.FirstOrDefault()?.Codec
                         ?? Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        return new SubtitleSourceAnalysis(
            filePath,
            Path.GetFileName(filePath),
            sizeBytes,
            duration,
            formatHint,
            tracks);
    }

    public async Task ExtractAsync(
        string sourcePath,
        string outputPath,
        int subtitleStreamIndex,
        SubtitleExportFormat exportFormat,
        IProgress<SubtitleExtractProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("File was not found.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var codecArg = exportFormat switch
        {
            SubtitleExportFormat.SubRip => "-c:s subrip",
            SubtitleExportFormat.WebVtt => "-c:s webvtt",
            SubtitleExportFormat.Ass => "-c:s ass",
            SubtitleExportFormat.Copy => "-c:s copy",
            _ => throw new ArgumentOutOfRangeException(nameof(exportFormat), exportFormat, null)
        };

        var conversion = FFmpeg.Conversions.New();
        conversion.SetOverwriteOutput(true);
        conversion.AddParameter($"-i \"{sourcePath}\"", ParameterPosition.PostInput);
        conversion.AddParameter($"-map 0:{subtitleStreamIndex.ToString(CultureInfo.InvariantCulture)}", ParameterPosition.PostInput);
        conversion.AddParameter(codecArg, ParameterPosition.PostInput);
        conversion.SetOutput(outputPath);

        conversion.OnProgress += (_, args) =>
        {
            var p01 = Math.Clamp(args.Percent / 100.0, 0, 1);
            progress?.Report(new SubtitleExtractProgressReport(p01, $"Extracting… {args.Percent:0}%"));
        };

        progress?.Report(new SubtitleExtractProgressReport(0, "Extracting subtitle…"));
        await conversion.Start(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Subtitle file was not created.");
        }

        progress?.Report(new SubtitleExtractProgressReport(1, "Done"));
    }
}
