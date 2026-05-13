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
        // Sequential probes + NVENC can be slow on first driver init; 45s was too tight.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
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

            // If FFmpeg lists NVENC but all probes failed (common: -f null muxer quirks on some builds/drivers),
            // still expose NVENC — runtime encode uses MP4/MKV and typically works on real NVIDIA GPUs.
            if (nvencListed && !nvencOk)
            {
                nvencOk = true;
            }

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

    private static string BuildHardwareProbeArgumentsNullMuxer(string videoCodec) =>
        videoCodec switch
        {
            "h264_nvenc" or "hevc_nvenc" =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                $"-c:v {videoCodec} -preset p1 -an -f null -",
            "h264_qsv" or "hevc_qsv" =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 -pix_fmt nv12 " +
                $"-c:v {videoCodec} -an -f null -",
            _ =>
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                $"-c:v {videoCodec} -an -f null -"
        };

    /// <summary>
    /// NVENC often fails with <c>-f null</c> on Windows even when encoding to MP4 works. Try null, temp MP4, then NVENC + gpu 0.
    /// </summary>
    private static async Task<bool> TryHardwareEncodeProbeAsync(
        string ffmpegExe,
        string videoCodec,
        CancellationToken cancellationToken)
    {
        if (videoCodec is "h264_nvenc" or "hevc_nvenc")
        {
            if (await RunFfmpegProbeAsync(ffmpegExe, BuildHardwareProbeArgumentsNullMuxer(videoCodec), cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }

            var tempMp4 = Path.Combine(Path.GetTempPath(), $"mediatools_nvprobe_{Guid.NewGuid():N}.mp4");
            try
            {
                var fileArgs =
                    "-hide_banner -nostats -loglevel error -y " +
                    "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                    $"-c:v {videoCodec} -preset p1 -an ";
                if (await RunFfmpegProbeAsync(
                            ffmpegExe,
                            $"{fileArgs}\"{tempMp4}\"",
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }

                var gpu0Args =
                    "-hide_banner -nostats -loglevel error -y " +
                    "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 -pix_fmt yuv420p " +
                    $"-c:v {videoCodec} -gpu 0 -preset p1 -an ";
                return await RunFfmpegProbeAsync(
                        ffmpegExe,
                        $"{gpu0Args}\"{tempMp4}\"",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDeleteFile(tempMp4);
            }
        }

        if (await RunFfmpegProbeAsync(ffmpegExe, BuildHardwareProbeArgumentsNullMuxer(videoCodec), cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        var tempOut = Path.Combine(Path.GetTempPath(), $"mediatools_hwprobe_{Guid.NewGuid():N}.mp4");
        try
        {
            var extra = videoCodec.Contains("qsv", StringComparison.OrdinalIgnoreCase)
                ? "-pix_fmt nv12 "
                : "-pix_fmt yuv420p ";
            var args =
                "-hide_banner -nostats -loglevel error -y " +
                "-f lavfi -i color=c=black:s=320x240:d=0.04 -frames:v 1 " +
                extra +
                $"-c:v {videoCodec} -an ";
            return await RunFfmpegProbeAsync(ffmpegExe, $"{args}\"{tempOut}\"", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempOut);
        }
    }

    private static void TryDeleteFile(string path)
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

    private static async Task<bool> RunFfmpegProbeAsync(
        string ffmpegExe,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = arguments,
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

    /// <summary>One-frame lavfi encode — tries null muxer then temp file for stubborn codecs.</summary>
    private static Task<bool> TryOneFrameHardwareEncodeAsync(
        string ffmpegExe,
        string videoCodec,
        CancellationToken cancellationToken) =>
        TryHardwareEncodeProbeAsync(ffmpegExe, videoCodec, cancellationToken);

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
