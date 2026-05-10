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
            bool has(string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);

            // FFmpeg lists encoders like " V....D h264_nvenc"
            var nvenc = has("h264_nvenc") || has("hevc_nvenc");
            var amf = has("h264_amf") || has("hevc_amf");
            var qsv = has("h264_qsv") || has("hevc_qsv");

            return new VideoEncoderScanResult(nvenc, amf, qsv);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            return new VideoEncoderScanResult(false, false, false);
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
