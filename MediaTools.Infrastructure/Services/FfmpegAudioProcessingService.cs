using System.Globalization;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Events;

namespace MediaTools.Infrastructure.Services;

public sealed class FfmpegAudioProcessingService(IVideoCompressionService videoCompressionService) : IAudioProcessingService
{
    public async Task<AudioTrackInfo> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file was not found.", filePath);
        }

        var info = await FFmpeg.GetMediaInfo(filePath, cancellationToken).ConfigureAwait(false);
        var a = info.AudioStreams.FirstOrDefault();
        if (a is null)
        {
            throw new InvalidOperationException("No audio stream was found in this file.");
        }

        var fileName = Path.GetFileName(filePath);
        var sizeBytes = info.Size > 0 ? info.Size : new FileInfo(filePath).Length;
        var duration = a.Duration > TimeSpan.Zero ? a.Duration : info.Duration;
        int? kbps = a.Bitrate > 0 ? (int)(a.Bitrate / 1000) : null;

        return new AudioTrackInfo(
            filePath,
            fileName,
            sizeBytes,
            duration,
            a.Codec,
            a.SampleRate,
            a.Channels,
            kbps);
    }

    public async Task ProcessAsync(
        string sourcePath,
        string outputPath,
        AudioEnhanceSettings settings,
        IProgress<AudioProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var conversion = FFmpeg.Conversions.New();
        conversion.SetOverwriteOutput(true);
        conversion.AddParameter($"-i \"{sourcePath}\"", ParameterPosition.PostInput);
        conversion.AddParameter("-vn", ParameterPosition.PostInput);
        conversion.AddParameter("-sn", ParameterPosition.PostInput);
        conversion.AddParameter("-dn", ParameterPosition.PostInput);

        var filterParts = new List<string>();
        if (settings.ClarityBoost)
        {
            filterParts.Add("highpass=f=80");
            filterParts.Add("equalizer=f=6500:width_type=h:width=2800:g=1.2");
        }

        if (settings.VolumePercent != 100)
        {
            var lin = Math.Clamp(settings.VolumePercent / 100.0, 0.05, 4.0);
            filterParts.Add($"volume={lin.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (settings.NormalizeLoudness)
        {
            filterParts.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        if (filterParts.Count > 0)
        {
            conversion.AddParameter($"-af \"{string.Join(",", filterParts)}\"", ParameterPosition.PostInput);
        }

        var ar = MapSampleRate(settings.SampleRate);
        if (ar is { } hz)
        {
            conversion.AddParameter($"-ar {hz}", ParameterPosition.PostInput);
        }

        var bitrate = Math.Clamp(settings.BitrateKbps, 64, 320);
        switch (settings.TargetFormat)
        {
            case AudioExportFormat.Mp3:
                conversion.AddParameter("-c:a libmp3lame", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {bitrate}k", ParameterPosition.PostInput);
                break;
            case AudioExportFormat.M4aAac:
                conversion.AddParameter("-c:a aac", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {bitrate}k", ParameterPosition.PostInput);
                break;
            case AudioExportFormat.Flac:
                conversion.AddParameter("-c:a flac", ParameterPosition.PostInput);
                conversion.AddParameter("-compression_level 8", ParameterPosition.PostInput);
                break;
            case AudioExportFormat.OggOpus:
                conversion.AddParameter("-c:a libopus", ParameterPosition.PostInput);
                conversion.AddParameter($"-b:a {Math.Clamp(bitrate, 64, 256)}k", ParameterPosition.PostInput);
                break;
            case AudioExportFormat.Wav:
                conversion.AddParameter("-c:a pcm_s16le", ParameterPosition.PostInput);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.TargetFormat, null);
        }

        conversion.SetOutput(outputPath);

        conversion.OnProgress += (_, args) =>
        {
            var p01 = Math.Clamp(args.Percent / 100.0, 0, 1);
            progress?.Report(new AudioProgressReport(p01, $"Encoding… {args.Percent:0}%"));
        };

        try
        {
            await conversion.Start(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        progress?.Report(new AudioProgressReport(1, "Done"));

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Output file was not created.");
        }
    }

    private static int? MapSampleRate(AudioSampleRateOption option) =>
        option switch
        {
            AudioSampleRateOption.Hz44100 => 44100,
            AudioSampleRateOption.Hz48000 => 48000,
            _ => null
        };
}
