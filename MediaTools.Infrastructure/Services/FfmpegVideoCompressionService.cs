using System.Diagnostics;
using System.Globalization;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Entities;
using MediaTools.Domain.ValueObjects;
using CompressionJobStatus = MediaTools.Domain.Enums.CompressionJobStatus;
using DomainAudioCodec = MediaTools.Domain.Enums.AudioCodec;
using DomainEncodePreset = MediaTools.Domain.Enums.EncodePreset;
using DomainVideoCodec = MediaTools.Domain.Enums.VideoCodec;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using Xabe.FFmpeg.Events;

namespace MediaTools.Infrastructure.Services;

public sealed class FfmpegVideoCompressionService : IVideoCompressionService
{
    private readonly object _sync = new();
    private bool _toolsReady;

    public async Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_toolsReady)
            {
                return;
            }
        }

        var ffmpegDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        Directory.CreateDirectory(ffmpegDirectory);

        var ffmpegExe = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
        var ffprobeExe = Path.Combine(ffmpegDirectory, "ffprobe.exe");

        if (!File.Exists(ffmpegExe) || !File.Exists(ffprobeExe))
        {
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegDirectory)
                .ConfigureAwait(false);
        }

        FFmpeg.SetExecutablesPath(
            ffmpegDirectory,
            "ffmpeg.exe",
            "ffprobe.exe",
            FileNameFilterMethod.Exact,
            CultureInfo.InvariantCulture);

        lock (_sync)
        {
            _toolsReady = true;
        }
    }

    public async Task<MediaFile> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Media file was not found.", filePath);
        }

        var info = await FFmpeg.GetMediaInfo(filePath, cancellationToken).ConfigureAwait(false);

        var fileName = Path.GetFileName(filePath);
        var sizeBytes = info.Size > 0 ? info.Size : new FileInfo(filePath).Length;
        var duration = info.Duration;

        var format = info.VideoStreams.FirstOrDefault()?.Codec ?? Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        return MediaFile.Create(filePath, fileName, sizeBytes, duration, format);
    }

    public async Task CompressAsync(
        CompressionJob job,
        IProgress<CompressionProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        var profile = job.Profile;
        var inputPath = job.SourceFile.FilePath;
        var outputPath = job.OutputPath;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        Xabe.FFmpeg.IConversion conversion = FFmpeg.Conversions.New();
        conversion.SetOverwriteOutput(true);

        conversion.AddParameter($"-i \"{inputPath}\"", ParameterPosition.PostInput);

        conversion.AddParameter($"-c:v {MapVideoCodec(profile.VideoCodec)}", ParameterPosition.PostInput);
        AddEncoderPreset(conversion, profile);

        AddCrfOrQuality(conversion, profile);

        if (profile.TargetWidth is { } w && profile.TargetHeight is { } h)
        {
            conversion.AddParameter($"-vf scale={w}:{h}", ParameterPosition.PostInput);
        }

        if (profile.RemoveAudio)
        {
            conversion.AddParameter("-an", ParameterPosition.PostInput);
        }
        else
        {
            AddAudioParameters(conversion, profile);
        }

        if (ShouldUseFastStart(profile))
        {
            conversion.AddParameter("-movflags +faststart", ParameterPosition.PostInput);
        }

        conversion.SetOutput(outputPath);

        var sw = Stopwatch.StartNew();

        conversion.OnProgress += (_, args) =>
        {
            ReportProgress(job, progress, sw.Elapsed, args);
        };

        try
        {
            await conversion.Start(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Cancel(DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex)
        {
            job.Fail(ex.Message, DateTimeOffset.UtcNow);
            throw;
        }

        if (job.Status == CompressionJobStatus.Cancelled)
        {
            return;
        }

        if (!File.Exists(outputPath))
        {
            job.Fail("Output file was not created.", DateTimeOffset.UtcNow);
            return;
        }

        var outputLength = new FileInfo(outputPath).Length;
        job.Complete(outputLength, DateTimeOffset.UtcNow);

        progress?.Report(new CompressionProgressReport(
            Percent01: 1,
            Elapsed: sw.Elapsed,
            EstimatedRemaining: TimeSpan.Zero,
            CurrentStepDescription: "Finished encoding"));
    }

    private static void ReportProgress(
        CompressionJob job,
        IProgress<CompressionProgressReport>? progress,
        TimeSpan elapsed,
        ConversionProgressEventArgs args)
    {
        var percent01 = Math.Clamp(args.Percent / 100.0, 0, 1);
        job.UpdateProgress(percent01);

        TimeSpan? remaining = null;
        if (percent01 > 0.001)
        {
            var totalEstimated = TimeSpan.FromTicks((long)(elapsed.Ticks / percent01));
            remaining = totalEstimated - elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }
        }

        progress?.Report(new CompressionProgressReport(
            Percent01: percent01,
            Elapsed: elapsed,
            EstimatedRemaining: remaining,
            CurrentStepDescription: $"Encoding… {args.Percent:0}%"));
    }

    private static void AddEncoderPreset(Xabe.FFmpeg.IConversion conversion, CompressionProfile profile)
    {
        switch (profile.VideoCodec)
        {
            case DomainVideoCodec.H264:
            case DomainVideoCodec.H265_HEVC:
                conversion.AddParameter($"-preset {MapEncodePreset(profile.EncodePreset)}", ParameterPosition.PostInput);
                break;
            case DomainVideoCodec.AV1:
                conversion.AddParameter($"-preset {MapSvtAv1Preset(profile.EncodePreset)}", ParameterPosition.PostInput);
                break;
            case DomainVideoCodec.VP9:
                conversion.AddParameter("-row-mt 1", ParameterPosition.PostInput);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile.VideoCodec, null);
        }
    }

    private static string MapSvtAv1Preset(DomainEncodePreset preset) =>
        preset switch
        {
            DomainEncodePreset.UltraFast or DomainEncodePreset.SuperFast or DomainEncodePreset.VeryFast => "12",
            DomainEncodePreset.Faster or DomainEncodePreset.Fast => "10",
            DomainEncodePreset.Medium => "8",
            DomainEncodePreset.Slow => "6",
            DomainEncodePreset.Slower or DomainEncodePreset.VerySlow => "4",
            _ => "8"
        };

    private static void AddCrfOrQuality(Xabe.FFmpeg.IConversion conversion, CompressionProfile profile) =>
        conversion.AddParameter($"-crf {profile.Crf}", ParameterPosition.PostInput);

    private static void AddAudioParameters(Xabe.FFmpeg.IConversion conversion, CompressionProfile profile)
    {
        switch (profile.AudioCodec)
        {
            case DomainAudioCodec.Copy:
                conversion.AddParameter("-c:a copy", ParameterPosition.PostInput);
                break;
            case DomainAudioCodec.AAC:
                conversion.AddParameter("-c:a aac", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {profile.AudioBitrateKbps}k", ParameterPosition.PostInput);
                break;
            case DomainAudioCodec.MP3:
                conversion.AddParameter("-c:a libmp3lame", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {profile.AudioBitrateKbps}k", ParameterPosition.PostInput);
                break;
            case DomainAudioCodec.Opus:
                conversion.AddParameter("-c:a libopus", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {profile.AudioBitrateKbps}k", ParameterPosition.PostInput);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile.AudioCodec, null);
        }
    }

    private static bool ShouldUseFastStart(CompressionProfile profile)
    {
        var ext = profile.OutputFileExtension.ToLowerInvariant();
        return ext is ".mp4" or ".m4v" or ".mov";
    }

    private static string MapVideoCodec(DomainVideoCodec codec) =>
        codec switch
        {
            DomainVideoCodec.H264 => "libx264",
            DomainVideoCodec.H265_HEVC => "libx265",
            DomainVideoCodec.AV1 => "libsvtav1",
            DomainVideoCodec.VP9 => "libvpx-vp9",
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null)
        };

    private static string MapEncodePreset(DomainEncodePreset preset) =>
        preset switch
        {
            DomainEncodePreset.UltraFast => "ultrafast",
            DomainEncodePreset.SuperFast => "superfast",
            DomainEncodePreset.VeryFast => "veryfast",
            DomainEncodePreset.Faster => "faster",
            DomainEncodePreset.Fast => "fast",
            DomainEncodePreset.Medium => "medium",
            DomainEncodePreset.Slow => "slow",
            DomainEncodePreset.Slower => "slower",
            DomainEncodePreset.VerySlow => "veryslow",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
        };
}
