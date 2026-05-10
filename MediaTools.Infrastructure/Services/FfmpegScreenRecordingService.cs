using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Screen recorder backed by the bundled FFmpeg binary using gdigrab for video
/// and optional dshow for microphone audio. Designed for Windows only.
/// </summary>
public sealed partial class FfmpegScreenRecordingService(IVideoCompressionService videoCompressionService)
    : IScreenRecordingService
{
    public async Task<IReadOnlyList<AudioInputDeviceDto>> GetAudioInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            return Array.Empty<AudioInputDeviceDto>();
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                sb.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Array.Empty<AudioInputDeviceDto>();
        }

        return ParseAudioDevices(sb.ToString());
    }

    public async Task<ScreenRecordingResult> RecordAsync(
        string outputPath,
        ScreenRecordingSettings settings,
        IProgress<ScreenRecordingProgressReport> progress,
        CancellationToken stopSignal,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        var ffmpegPath = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("FFmpeg executable could not be located.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var arguments = BuildArguments(outputPath, settings);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            ReportFromFfmpegLine(progress, e.Data);
        };

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            progress.Report(new ScreenRecordingProgressReport(
                Elapsed: TimeSpan.Zero,
                CurrentSizeBytes: null,
                StepDescription: "Recording…"));
        }
        catch (Exception ex)
        {
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: $"Failed to start FFmpeg: {ex.Message}",
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: TimeSpan.Zero);
        }

        var hardCancel = false;
        try
        {
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stopSignal, cancellationToken);
            try
            {
                await Task.Delay(Timeout.Infinite, combinedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                hardCancel = cancellationToken.IsCancellationRequested;
            }

            if (!process.HasExited)
            {
                if (hardCancel)
                {
                    TryKill(process);
                }
                else
                {
                    await StopGracefullyAsync(process).ConfigureAwait(false);
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: ex.Message,
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: sw.Elapsed);
        }

        sw.Stop();

        if (hardCancel)
        {
            TryDelete(outputPath);
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: true,
                ErrorMessage: null,
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: sw.Elapsed);
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            return new ScreenRecordingResult(
                IsSuccess: false,
                IsCancelled: false,
                ErrorMessage: ExtractFfmpegError(stderr.ToString()),
                OutputFilePath: null,
                OutputFileSizeBytes: null,
                TotalDuration: sw.Elapsed);
        }

        var size = new FileInfo(outputPath).Length;
        progress.Report(new ScreenRecordingProgressReport(
            Elapsed: sw.Elapsed,
            CurrentSizeBytes: size,
            StepDescription: "Saved"));

        return new ScreenRecordingResult(
            IsSuccess: true,
            IsCancelled: false,
            ErrorMessage: null,
            OutputFilePath: outputPath,
            OutputFileSizeBytes: size,
            TotalDuration: sw.Elapsed);
    }

    /// <summary>
    /// gdigrab (GDI) rarely sustains &gt; ~60 real captures/sec on Windows; higher -framerate often yields empty or broken video.
    /// We capture at most this rate, then use the fps filter to reach the user's target constant frame rate (duplicated frames).
    /// </summary>
    private const int GdigrabPracticalMaxCaptureFps = 60;

    private static string BuildArguments(string outputPath, ScreenRecordingSettings settings)
    {
        var ci = CultureInfo.InvariantCulture;
        var outputFps = Math.Clamp(settings.FrameRate, 5, 120);
        var captureFps = Math.Min(outputFps, GdigrabPracticalMaxCaptureFps);
        var crf = Math.Clamp(settings.Crf, 14, 40);

        var sb = new StringBuilder();
        sb.Append("-hide_banner -y ");

        sb.Append("-f gdigrab ");
        sb.Append(ci, $"-framerate {captureFps} ");
        sb.Append(ci, $"-draw_mouse {(settings.CaptureCursor ? 1 : 0)} ");
        sb.Append("-rtbufsize 256M ");
        sb.Append("-thread_queue_size 1024 ");

        if (settings.Region == ScreenRecordingRegion.Custom
            || settings.Region == ScreenRecordingRegion.PrimaryMonitor)
        {
            var w = EvenClamp(settings.CaptureWidth);
            var h = EvenClamp(settings.CaptureHeight);
            sb.Append(ci, $"-offset_x {settings.OffsetX} -offset_y {settings.OffsetY} ");
            sb.Append(ci, $"-video_size {w}x{h} ");
        }

        sb.Append("-i desktop ");

        if (settings.IncludeMicrophone && !string.IsNullOrWhiteSpace(settings.MicrophoneDeviceName))
        {
            sb.Append("-f dshow -rtbufsize 256M ");
            sb.Append(ci, $"-i audio=\"{EscapeForCommandLine(settings.MicrophoneDeviceName)}\" ");
        }

        if (outputFps != captureFps)
        {
            sb.Append(ci, $"-vf fps={outputFps} ");
        }

        AppendH264VideoEncode(sb, settings.VideoEncoder, crf);

        if (settings.IncludeMicrophone && !string.IsNullOrWhiteSpace(settings.MicrophoneDeviceName))
        {
            sb.Append("-c:a aac -b:a 192k ");
        }

        if (settings.OutputFormat == ScreenRecordingOutputFormat.Mp4)
        {
            sb.Append("-movflags +faststart ");
        }

        sb.Append(ci, $"\"{outputPath}\"");
        return sb.ToString();
    }

    /// <summary>Matches <see cref="FfmpegVideoEnhanceService"/> quality mapping; <paramref name="quality"/> is the UI CRF (14–40).</summary>
    private static void AppendH264VideoEncode(StringBuilder sb, VideoHardwareEncoderKind encoder, int quality)
    {
        var ci = CultureInfo.InvariantCulture;
        var q = Math.Clamp(quality, 14, 40);
        switch (encoder)
        {
            case VideoHardwareEncoderKind.Nvenc:
                sb.Append(ci, $"-c:v h264_nvenc -preset p4 -cq {q} -pix_fmt yuv420p ");
                break;
            case VideoHardwareEncoderKind.Amf:
                sb.Append(ci, $"-c:v h264_amf -quality balanced -rc cqp -qp_i {q} -qp_p {q} -pix_fmt yuv420p ");
                break;
            case VideoHardwareEncoderKind.QuickSync:
                sb.Append(ci, $"-c:v h264_qsv -preset medium -global_quality {q} -pix_fmt yuv420p ");
                break;
            default:
                sb.Append("-c:v libx264 -preset veryfast -pix_fmt yuv420p ");
                sb.Append(ci, $"-crf {q} ");
                break;
        }
    }

    private static int EvenClamp(int v)
    {
        var x = Math.Max(2, v);
        return x % 2 == 0 ? x : x - 1;
    }

    private static string EscapeForCommandLine(string value) =>
        value.Replace("\"", "\\\"");

    private static async Task StopGracefullyAsync(Process process)
    {
        try
        {
            await process.StandardInput.WriteAsync('q').ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore — process may have already exited
        }

        try
        {
            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void ReportFromFfmpegLine(IProgress<ScreenRecordingProgressReport> progress, string line)
    {
        var time = TimeRegex().Match(line);
        var size = SizeRegex().Match(line);

        if (!time.Success && !size.Success)
        {
            return;
        }

        TimeSpan elapsed = TimeSpan.Zero;
        if (time.Success
            && int.TryParse(time.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)
            && int.TryParse(time.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)
            && double.TryParse(time.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ss))
        {
            elapsed = TimeSpan.FromSeconds(hh * 3600 + mm * 60 + ss);
        }

        long? sizeBytes = null;
        if (size.Success
            && long.TryParse(size.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
        {
            sizeBytes = kib * 1024;
        }

        progress.Report(new ScreenRecordingProgressReport(
            Elapsed: elapsed,
            CurrentSizeBytes: sizeBytes,
            StepDescription: "Recording…"));
    }

    private static string? ResolveFfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    private static IReadOnlyList<AudioInputDeviceDto> ParseAudioDevices(string ffmpegOutput)
    {
        var list = new List<AudioInputDeviceDto>();
        var inAudioSection = false;

        foreach (var raw in ffmpegOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            var nameMatch = DeviceNameRegex().Match(line);
            if (nameMatch.Success)
            {
                var name = nameMatch.Groups[1].Value;
                if (!list.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new AudioInputDeviceDto(name));
                }
            }
        }

        return list;
    }

    private static string ExtractFfmpegError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "FFmpeg did not produce an output file.";
        }

        var lines = stderr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0
                && (l.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToArray();

        return lines.Length > 0
            ? string.Join(" | ", lines)
            : "FFmpeg failed. See logs for details.";
    }

    [GeneratedRegex("time=(\\d+):(\\d{2}):(\\d{2}(?:\\.\\d+)?)")]
    private static partial Regex TimeRegex();

    [GeneratedRegex("size=\\s*(\\d+)\\s*[kK]i?B")]
    private static partial Regex SizeRegex();

    [GeneratedRegex("\"([^\"]+)\"\\s*\\((?:audio|Audio)\\)|\\[dshow.*?\\]\\s*\"([^\"]+)\"")]
    private static partial Regex DeviceNameRegex();
}
