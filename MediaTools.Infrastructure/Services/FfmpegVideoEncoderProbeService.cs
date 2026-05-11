using System.Diagnostics;
using System.IO;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;

namespace MediaTools.Infrastructure.Services;

public sealed class FfmpegVideoEncoderProbeService(IVideoCompressionService videoCompressionService) : IVideoEncoderProbeService
{
    public async Task<VideoEncoderScanResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        var ffmpegExe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpegExe))
        {
            return new VideoEncoderScanResult(false, false, false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));
        var probeToken = timeoutCts.Token;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            Arguments = "-hide_banner -encoders",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // FFmpeg writes to both streams; reading one before the other can fill a pipe buffer and deadlock the child.
        var readOut = proc.StandardOutput.ReadToEndAsync(probeToken);
        var readErr = proc.StandardError.ReadToEndAsync(probeToken);
        try
        {
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            await proc.WaitForExitAsync(probeToken).ConfigureAwait(false);

            var stdout = await readOut.ConfigureAwait(false);
            var stderr = await readErr.ConfigureAwait(false);
            var text = stdout + "\n" + stderr;
            bool listed(string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);

            // `-encoders` only means the build supports the codec; NVENC/AMF/QSV still need matching hardware + drivers.
            var nvencListed = listed("h264_nvenc") || listed("hevc_nvenc");
            var amfListed = listed("h264_amf") || listed("hevc_amf");
            var qsvListed = listed("h264_qsv") || listed("hevc_qsv");

            // Run GPU probes one at a time — parallel ffmpeg processes can confuse some driver stacks.
            var nvencOk = nvencListed
                && await TryNvencHardwareWorksAsync(ffmpegExe, text, probeToken).ConfigureAwait(false);
            var amfOk = amfListed
                && await TryAmfHardwareWorksAsync(ffmpegExe, text, probeToken).ConfigureAwait(false);
            var qsvOk = qsvListed
                && await TryQsvHardwareWorksAsync(ffmpegExe, text, probeToken).ConfigureAwait(false);

            return new VideoEncoderScanResult(nvencOk, amfOk, qsvOk);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            return new VideoEncoderScanResult(false, false, false);
        }
    }

    private static bool ListedInEncodersOutput(string encodersText, string token) =>
        encodersText.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> TryNvencHardwareWorksAsync(
        string ffmpegExe,
        string encodersText,
        CancellationToken cancellationToken)
    {
        if (ListedInEncodersOutput(encodersText, "h264_nvenc")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "h264_nvenc", cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return ListedInEncodersOutput(encodersText, "hevc_nvenc")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "hevc_nvenc", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryAmfHardwareWorksAsync(
        string ffmpegExe,
        string encodersText,
        CancellationToken cancellationToken)
    {
        if (ListedInEncodersOutput(encodersText, "h264_amf")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "h264_amf", cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return ListedInEncodersOutput(encodersText, "hevc_amf")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "hevc_amf", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryQsvHardwareWorksAsync(
        string ffmpegExe,
        string encodersText,
        CancellationToken cancellationToken)
    {
        if (ListedInEncodersOutput(encodersText, "h264_qsv")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "h264_qsv", cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return ListedInEncodersOutput(encodersText, "hevc_qsv")
            && await TryOneFrameHardwareEncodeAsync(ffmpegExe, "hevc_qsv", cancellationToken).ConfigureAwait(false);
    }

    private static string BuildHardwareProbeArguments(string videoCodec) =>
        videoCodec switch
        {
            "h264_nvenc" or "hevc_nvenc" =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=64x64:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                $"-c:v {videoCodec} -f null -",
            "h264_qsv" or "hevc_qsv" =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=64x64:d=0.04 -frames:v 1 -pix_fmt nv12 " +
                $"-c:v {videoCodec} -f null -",
            _ =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=64x64:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                $"-c:v {videoCodec} -f null -"
        };

    /// <summary>One-frame lavfi encode to null muxer — fails fast when GPU/driver is missing.</summary>
    private static async Task<bool> TryOneFrameHardwareEncodeAsync(
        string ffmpegExe,
        string videoCodec,
        CancellationToken cancellationToken)
    {
        var args = BuildHardwareProbeArguments(videoCodec);

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        proc.Start();
        var readOut = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var readErr = proc.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return false;
        }
        catch
        {
            TryKill(proc);
            return false;
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
