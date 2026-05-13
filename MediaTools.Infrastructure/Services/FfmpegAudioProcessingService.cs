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

        var mediaInfo = await FFmpeg.GetMediaInfo(sourcePath, cancellationToken).ConfigureAwait(false);
        var audioStream = mediaInfo.AudioStreams.FirstOrDefault()
            ?? throw new InvalidOperationException("No audio stream was found in this file.");
        var channels = audioStream.Channels;

        var conversion = FFmpeg.Conversions.New();
        conversion.SetOverwriteOutput(true);
        conversion.AddParameter($"-i \"{sourcePath}\"", ParameterPosition.PostInput);
        conversion.AddParameter("-vn", ParameterPosition.PostInput);
        conversion.AddParameter("-sn", ParameterPosition.PostInput);
        conversion.AddParameter("-dn", ParameterPosition.PostInput);

        if (settings.Workspace == AudioEnhancerWorkspace.VocalRemover)
        {
            if (channels < 2)
            {
                throw new InvalidOperationException(
                    "Vocal remover needs a stereo track. This file does not have two channels.");
            }

            var fc = BuildVocalInstrumentalFilterComplex(settings);
            conversion.AddParameter($"-filter_complex \"{fc}\"", ParameterPosition.PostInput);
            conversion.AddParameter("-map \"[aout]\"", ParameterPosition.PostInput);
        }
        else
        {
            var af = BuildAfChain(settings);
            if (!string.IsNullOrEmpty(af))
            {
                conversion.AddParameter($"-af \"{af}\"", ParameterPosition.PostInput);
            }
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

    private static string BuildAfChain(AudioEnhanceSettings settings)
    {
        var filterParts = new List<string>();

        switch (settings.Workspace)
        {
            case AudioEnhancerWorkspace.EnhanceAndConvert:
                break;
            case AudioEnhancerWorkspace.NoiseReduction:
                filterParts.Add(BuildAfftdn(settings));
                break;
            case AudioEnhancerWorkspace.SilenceRemover:
                filterParts.Add(BuildSilenceRemove(settings));
                break;
            case AudioEnhancerWorkspace.VocalRemover:
                throw new InvalidOperationException("Internal: vocal remover must use filter_complex.");
            default:
                throw new ArgumentOutOfRangeException(nameof(settings));
        }

        if (settings.Workspace == AudioEnhancerWorkspace.EnhanceAndConvert && settings.ClarityBoost)
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

        return string.Join(",", filterParts);
    }

    private static string BuildAfftdn(AudioEnhanceSettings settings)
    {
        var s = Math.Clamp(settings.NoiseReductionStrength01, 0.05f, 1f);
        var nr = 6 + (int)Math.Round(42 * s);
        nr = Math.Clamp(nr, 3, 96);
        var nf = -40 + 15 * s;
        nf = Math.Clamp(nf, -80f, -15f);
        var nfStr = nf.ToString("0.#", CultureInfo.InvariantCulture);
        return $"afftdn=nr={nr}:nf={nfStr}";
    }

    private static string BuildSilenceRemove(AudioEnhanceSettings settings)
    {
        var minDur = Math.Clamp(settings.MinSilenceDurationSec, 0.02f, 10f);
        var window = Math.Clamp(settings.SilenceDetectionWindowSec, 0.005f, 2f);
        var thrDb = Math.Clamp(settings.SilenceThresholdDb, -90f, 0f);
        var dMin = minDur.ToString("0.###", CultureInfo.InvariantCulture);
        var dWin = window.ToString("0.###", CultureInfo.InvariantCulture);
        var thr = thrDb.ToString("0.#", CultureInfo.InvariantCulture) + "dB";
        return "silenceremove=start_periods=1"
            + $":start_duration={dWin}:start_threshold={thr}:detection=peak"
            + ":stop_periods=-1"
            + $":stop_duration={dMin}:stop_threshold={thr}"
            + $":window={dWin}";
    }

    /// <summary>
    /// Mid/side blend: higher strength favors side (L−R), lower favors mid (L+R) — stereo only.
    /// </summary>
    private static string BuildVocalInstrumentalFilterComplex(AudioEnhanceSettings settings)
    {
        var s = Math.Clamp(settings.VocalRemoverStrength01, 0f, 1f);
        var wMid = (1 - s).ToString("0.###", CultureInfo.InvariantCulture);
        var wSide = s.ToString("0.###", CultureInfo.InvariantCulture);

        var tail = new List<string>();
        if (settings.VolumePercent != 100)
        {
            var lin = Math.Clamp(settings.VolumePercent / 100.0, 0.05, 4.0);
            tail.Add($"volume={lin.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        if (settings.NormalizeLoudness)
        {
            tail.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        var tailStr = tail.Count > 0 ? string.Join(",", tail) : "volume=1";

        return "[0:a]asplit=2[msrc][ssrc];"
            + "[msrc]pan=mono|c0=0.5*c0+0.5*c1[mid];"
            + "[ssrc]pan=mono|c0=0.5*c0-0.5*c1[side];"
            + $"[mid][side]amix=inputs=2:weights={wMid} {wSide}:normalize=0[vm];"
            + $"[vm]{tailStr}[aout]";
    }

    private static int? MapSampleRate(AudioSampleRateOption option) =>
        option switch
        {
            AudioSampleRateOption.Hz44100 => 44100,
            AudioSampleRateOption.Hz48000 => 48000,
            _ => null
        };
}
