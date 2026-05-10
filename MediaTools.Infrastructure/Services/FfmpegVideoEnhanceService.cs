using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using Xabe.FFmpeg;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Single multi-feature video enhancer backed by the bundled FFmpeg binary:
/// watermark, speed change, reverse, stabilize (2-pass), color grading, crop/resize, and video-to-audio.
/// </summary>
public sealed class FfmpegVideoEnhanceService(IVideoCompressionService videoCompressionService) : IVideoEnhanceService
{
    public async Task<VideoSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File was not found.", filePath);
        }

        var info = await FFmpeg.GetMediaInfo(filePath, cancellationToken).ConfigureAwait(false);
        var video = info.VideoStreams.FirstOrDefault()
                    ?? throw new InvalidOperationException("No video stream found in file.");

        var sizeBytes = info.Size > 0 ? info.Size : new FileInfo(filePath).Length;

        return new VideoSourceAnalysis(
            FilePath: filePath,
            FileName: Path.GetFileName(filePath),
            FileSizeBytes: sizeBytes,
            Duration: info.Duration > TimeSpan.Zero ? info.Duration : TimeSpan.Zero,
            Width: video.Width,
            Height: video.Height,
            VideoCodec: video.Codec,
            HasAudio: info.AudioStreams.Any());
    }

    public async Task EnhanceAsync(
        string sourcePath,
        string outputPath,
        VideoEnhanceSettings settings,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file was not found.", sourcePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var analysis = await AnalyzeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var totalDurationSeconds = analysis.Duration.TotalSeconds;

        switch (settings.Operation)
        {
            case VideoEnhanceOperation.Watermark:
                await ApplyWatermarkAsync(sourcePath, outputPath, analysis, settings.Watermark!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.SpeedChange:
                await ApplySpeedAsync(sourcePath, outputPath, analysis, settings.Speed!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.Reverse:
                await ApplyReverseAsync(sourcePath, outputPath, analysis, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.Stabilize:
                await ApplyStabilizeAsync(sourcePath, outputPath, analysis, settings.Stabilizer!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.ColorGrading:
                await ApplyColorGradingAsync(sourcePath, outputPath, analysis, settings.ColorGrading!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.CropAndResize:
                await ApplyCropResizeAsync(sourcePath, outputPath, analysis, settings.CropResize!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.ExtractAudio:
                if (!analysis.HasAudio)
                {
                    throw new InvalidOperationException("Source video has no audio stream to extract.");
                }

                await ApplyExtractAudioAsync(sourcePath, outputPath, settings.ToAudio!, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.ExtractSubtitle:
                throw new InvalidOperationException("Subtitle extraction is not implemented in the video enhance pipeline.");

            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Operation, "Unknown operation.");
        }

        progress.Report(new VideoEnhanceProgressReport(1, "Done"));
    }

    public async Task<byte[]?> TryRenderEffectPreviewJpegAsync(
        string sourcePath,
        VideoEnhanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(sourcePath))
        {
            return null;
        }

        VideoSourceAnalysis analysis;
        try
        {
            analysis = await AnalyzeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var seekSeconds = analysis.Duration.TotalSeconds > 2
            ? 1.0
            : Math.Max(0.05, analysis.Duration.TotalSeconds * 0.15);

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("-hide_banner -nostats -loglevel error ");
        sb.Append(ci, $"-ss {seekSeconds.ToString("0.###", ci)} ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");

        switch (settings.Operation)
        {
            case VideoEnhanceOperation.ColorGrading when settings.ColorGrading is { } color:
            {
                var vf = BuildColorGradingFilter(color);
                sb.Append(ci, $"-vf \"{vf}\" ");
                break;
            }

            case VideoEnhanceOperation.CropAndResize when settings.CropResize is { } cr:
            {
                if (!cr.CropEnabled && !cr.ResizeEnabled)
                {
                    return null;
                }

                var vf = BuildCropResizeFilter(analysis, cr);
                if (string.IsNullOrEmpty(vf))
                {
                    return null;
                }

                sb.Append(ci, $"-vf \"{vf}\" ");
                break;
            }

            case VideoEnhanceOperation.Watermark when settings.Watermark is { } wm:
            {
                var opacity = Math.Clamp(wm.OpacityPercent, 0, 100) / 100.0;
                var sizePct = Math.Clamp(wm.SizePercent, 5, 90) / 100.0;
                var targetWmWidth = (int)Math.Max(16, Math.Round(analysis.Width * sizePct));
                if (targetWmWidth % 2 == 1)
                {
                    targetWmWidth--;
                }

                const int margin = 20;
                string filterComplex;
                if (wm.Source == WatermarkSourceKind.Image)
                {
                    if (string.IsNullOrWhiteSpace(wm.ImagePath) || !File.Exists(wm.ImagePath))
                    {
                        return null;
                    }

                    sb.Append(ci, $"-i \"{wm.ImagePath}\" ");
                    var pos = BuildOverlayPosition(wm.Position, margin);
                    filterComplex =
                        $"[1:v]scale={targetWmWidth.ToString(ci)}:-1,format=rgba," +
                        $"colorchannelmixer=aa={opacity.ToString("0.###", ci)}[wm];" +
                        $"[0:v][wm]overlay={pos}[v]";
                }
                else
                {
                    var rawText = string.IsNullOrWhiteSpace(wm.Text) ? "MediaTools" : wm.Text;
                    var fontSize = Math.Max(12, (int)Math.Round(analysis.Height * sizePct * 0.35));
                    var pos = BuildDrawtextPosition(wm.Position, margin);
                    filterComplex =
                        $"[0:v]drawtext=text='{EscapeDrawtextText(rawText)}':" +
                        $"fontcolor=white@{opacity.ToString("0.###", ci)}:" +
                        $"fontsize={fontSize.ToString(ci)}:" +
                        $"box=1:boxcolor=black@{(opacity * 0.5).ToString("0.###", ci)}:" +
                        $"boxborderw=10:" +
                        $"{pos}[v]";
                }

                sb.Append(ci, $"-filter_complex \"{filterComplex}\" ");
                sb.Append("-map \"[v]\" ");
                break;
            }

            default:
                return null;
        }

        sb.Append("-an -frames:v 1 -f image2pipe -vcodec mjpeg -q:v 4 - ");

        try
        {
            return await RunFfmpegStdoutBytesAsync(sb.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildColorGradingFilter(VideoColorGradingSettings color)
    {
        var ci = CultureInfo.InvariantCulture;
        var b = Math.Clamp(color.Brightness, -1, 1);
        var c = Math.Clamp(color.Contrast, 0, 2);
        var s = Math.Clamp(color.Saturation, 0, 3);
        var g = Math.Clamp(color.Gamma, 0.1, 10);
        var h = Math.Clamp(color.Hue, -180, 180);

        return
            $"eq=brightness={b.ToString("0.###", ci)}:" +
            $"contrast={c.ToString("0.###", ci)}:" +
            $"saturation={s.ToString("0.###", ci)}:" +
            $"gamma={g.ToString("0.###", ci)}," +
            $"hue=h={h.ToString("0.###", ci)}";
    }

    private static string BuildCropResizeFilter(VideoSourceAnalysis analysis, VideoCropResizeSettings cr)
    {
        var ci = CultureInfo.InvariantCulture;
        var filters = new List<string>();

        if (cr.CropEnabled)
        {
            var x = Math.Clamp(cr.CropX, 0, Math.Max(0, analysis.Width - 16));
            var y = Math.Clamp(cr.CropY, 0, Math.Max(0, analysis.Height - 16));
            var w = Math.Clamp(cr.CropWidth, 16, analysis.Width - x);
            var h = Math.Clamp(cr.CropHeight, 16, analysis.Height - y);
            if (w % 2 == 1)
            {
                w--;
            }

            if (h % 2 == 1)
            {
                h--;
            }

            filters.Add($"crop={w.ToString(ci)}:{h.ToString(ci)}:{x.ToString(ci)}:{y.ToString(ci)}");
        }

        if (cr.ResizeEnabled)
        {
            var rw = cr.ResizeWidth ?? -2;
            var rh = cr.ResizeHeight ?? -2;
            if (rw > 0 && rw % 2 == 1)
            {
                rw--;
            }

            if (rh > 0 && rh % 2 == 1)
            {
                rh--;
            }

            filters.Add($"scale={rw.ToString(ci)}:{rh.ToString(ci)}:flags=lanczos");
        }

        return filters.Count == 0 ? string.Empty : string.Join(",", filters);
    }

    private static async Task<byte[]> RunFfmpegStdoutBytesAsync(string arguments, CancellationToken ct)
    {
        var ffmpegPath = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("FFmpeg executable could not be located.");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        await using var stdout = process.StandardOutput.BaseStream;
        await using var ms = new MemoryStream();
        try
        {
            await stdout.CopyToAsync(ms, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}: {ExtractError(stderr.ToString())}");
        }

        var bytes = ms.ToArray();
        if (bytes.Length < 64)
        {
            throw new InvalidOperationException("Preview output was empty.");
        }

        return bytes;
    }

    private async Task ApplyWatermarkAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoWatermarkSettings wm,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var opacity = Math.Clamp(wm.OpacityPercent, 0, 100) / 100.0;
        var sizePct = Math.Clamp(wm.SizePercent, 5, 90) / 100.0;
        var targetWmWidth = (int)Math.Max(16, Math.Round(analysis.Width * sizePct));
        if (targetWmWidth % 2 == 1)
        {
            targetWmWidth--;
        }

        const int margin = 20;

        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");

        string filterComplex;
        var mapAudio = analysis.HasAudio ? "-map 0:a -c:a copy " : string.Empty;

        if (wm.Source == WatermarkSourceKind.Image)
        {
            if (string.IsNullOrWhiteSpace(wm.ImagePath) || !File.Exists(wm.ImagePath))
            {
                throw new FileNotFoundException("Watermark image was not found.", wm.ImagePath);
            }

            sb.Append(ci, $"-i \"{wm.ImagePath}\" ");

            var pos = BuildOverlayPosition(wm.Position, margin);
            filterComplex =
                $"[1:v]scale={targetWmWidth.ToString(ci)}:-1,format=rgba," +
                $"colorchannelmixer=aa={opacity.ToString("0.###", ci)}[wm];" +
                $"[0:v][wm]overlay={pos}[v]";
        }
        else
        {
            var rawText = string.IsNullOrWhiteSpace(wm.Text) ? "MediaTools" : wm.Text;
            var fontSize = Math.Max(12, (int)Math.Round(analysis.Height * sizePct * 0.35));
            var pos = BuildDrawtextPosition(wm.Position, margin);

            filterComplex =
                $"[0:v]drawtext=text='{EscapeDrawtextText(rawText)}':" +
                $"fontcolor=white@{opacity.ToString("0.###", ci)}:" +
                $"fontsize={fontSize.ToString(ci)}:" +
                $"box=1:boxcolor=black@{(opacity * 0.5).ToString("0.###", ci)}:" +
                $"boxborderw=10:" +
                $"{pos}[v]";
        }

        sb.Append(ci, $"-filter_complex \"{filterComplex}\" ");
        sb.Append("-map \"[v]\" ");
        sb.Append(mapAudio);
        sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart ");
        sb.Append(ci, $"\"{outputPath}\"");

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Embedding watermark", ct).ConfigureAwait(false);
    }

    private async Task ApplySpeedAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoSpeedSettings speed,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var factor = Math.Clamp(speed.Factor, 0.25, 4.0);
        var pts = (1.0 / factor).ToString("0.######", ci);
        var videoFilter = $"setpts={pts}*PTS";

        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");

        if (analysis.HasAudio)
        {
            string audioFilter;
            if (speed.PreservePitch)
            {
                audioFilter = BuildAtempoChain(factor);
            }
            else
            {
                audioFilter = $"asetrate=44100*{factor.ToString("0.######", ci)},aresample=44100";
            }

            sb.Append(ci,
                $"-filter_complex \"[0:v]{videoFilter}[v];[0:a]{audioFilter}[a]\" -map \"[v]\" -map \"[a]\" ");
            sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart ");
        }
        else
        {
            sb.Append(ci, $"-vf \"{videoFilter}\" ");
            sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -an -movflags +faststart ");
        }

        sb.Append(ci, $"\"{outputPath}\"");
        var stepName = $"Speed {factor.ToString("0.##", ci)}x";

        // Output duration shrinks by factor when speeding up; total progress = output_time / output_duration
        var outputDuration = totalDurationSeconds > 0 ? totalDurationSeconds / factor : 0;
        await RunFfmpegAsync(sb.ToString(), outputDuration, progress, stepName, ct).ConfigureAwait(false);
    }

    private async Task ApplyReverseAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");

        if (analysis.HasAudio)
        {
            sb.Append("-vf reverse -af areverse ");
            sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k ");
        }
        else
        {
            sb.Append("-vf reverse -an ");
            sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p ");
        }

        sb.Append("-movflags +faststart ");
        sb.Append(ci, $"\"{outputPath}\"");

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Reversing", ct).ConfigureAwait(false);
    }

    private async Task ApplyStabilizeAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoStabilizerSettings stab,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var smoothing = Math.Clamp(stab.Smoothing, 1, 100);
        var zoom = Math.Clamp(stab.Zoom, 0, 5);

        var trfPath = Path.Combine(Path.GetTempPath(), $"mediatools_vidstab_{Guid.NewGuid():N}.trf");
        try
        {
            var sb1 = new StringBuilder();
            sb1.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
            sb1.Append(ci, $"-i \"{sourcePath}\" ");
            sb1.Append(ci,
                $"-vf \"vidstabdetect=stepsize=6:shakiness=8:accuracy=9:result={EscapeFilterPath(trfPath)}\" ");
            sb1.Append("-f null -");

            await RunFfmpegAsync(sb1.ToString(), totalDurationSeconds, progress, "Analyzing motion (1/2)", ct).ConfigureAwait(false);

            var sb2 = new StringBuilder();
            sb2.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
            sb2.Append(ci, $"-i \"{sourcePath}\" ");
            sb2.Append(ci,
                $"-vf \"vidstabtransform=smoothing={smoothing.ToString(ci)}:" +
                $"zoom={zoom.ToString("0.##", ci)}:input={EscapeFilterPath(trfPath)},unsharp=5:5:0.8:3:3:0.4\" ");

            if (analysis.HasAudio)
            {
                sb2.Append("-c:a copy ");
            }
            else
            {
                sb2.Append("-an ");
            }

            sb2.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart ");
            sb2.Append(ci, $"\"{outputPath}\"");

            await RunFfmpegAsync(sb2.ToString(), totalDurationSeconds, progress, "Stabilizing (2/2)", ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(trfPath);
        }
    }

    private async Task ApplyColorGradingAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoColorGradingSettings color,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var filter = BuildColorGradingFilter(color);

        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");
        sb.Append(ci, $"-vf \"{filter}\" ");

        if (analysis.HasAudio)
        {
            sb.Append("-c:a copy ");
        }
        else
        {
            sb.Append("-an ");
        }

        sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart ");
        sb.Append(ci, $"\"{outputPath}\"");

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Color grading", ct).ConfigureAwait(false);
    }

    private async Task ApplyCropResizeAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoCropResizeSettings cr,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        if (!cr.CropEnabled && !cr.ResizeEnabled)
        {
            throw new InvalidOperationException("Enable at least one of crop or resize.");
        }

        var ci = CultureInfo.InvariantCulture;
        var vf = BuildCropResizeFilter(analysis, cr);
        if (string.IsNullOrEmpty(vf))
        {
            throw new InvalidOperationException("Enable at least one of crop or resize.");
        }

        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");
        sb.Append(ci, $"-vf \"{vf}\" ");

        if (analysis.HasAudio)
        {
            sb.Append("-c:a copy ");
        }
        else
        {
            sb.Append("-an ");
        }

        sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart ");
        sb.Append(ci, $"\"{outputPath}\"");

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Crop & resize", ct).ConfigureAwait(false);
    }

    private async Task ApplyExtractAudioAsync(
        string sourcePath,
        string outputPath,
        VideoToAudioSettings audioSettings,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -nostats -loglevel error -progress pipe:1 ");
        sb.Append(ci, $"-i \"{sourcePath}\" ");
        sb.Append("-vn -map 0:a:0 ");

        switch (audioSettings.Format)
        {
            case AudioExportFormat.Mp3:
                sb.Append(ci, $"-c:a libmp3lame -b:a {audioSettings.BitrateKbps.ToString(ci)}k ");
                break;
            case AudioExportFormat.M4aAac:
                sb.Append(ci, $"-c:a aac -b:a {audioSettings.BitrateKbps.ToString(ci)}k ");
                break;
            case AudioExportFormat.Flac:
                sb.Append("-c:a flac ");
                break;
            case AudioExportFormat.OggOpus:
                sb.Append(ci, $"-c:a libopus -b:a {audioSettings.BitrateKbps.ToString(ci)}k ");
                break;
            case AudioExportFormat.Wav:
                sb.Append("-c:a pcm_s16le ");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(audioSettings), audioSettings.Format, null);
        }

        sb.Append(ci, $"\"{outputPath}\"");

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Extracting audio", ct).ConfigureAwait(false);
    }

    private static async Task RunFfmpegAsync(
        string arguments,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        string stepName,
        CancellationToken ct)
    {
        var ffmpegPath = ResolveFfmpegPath()
            ?? throw new InvalidOperationException("FFmpeg executable could not be located.");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            ParseProgressLine(e.Data, totalDurationSeconds, progress, stepName);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        progress.Report(new VideoEnhanceProgressReport(0, $"{stepName}…"));

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
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

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}: {ExtractError(stderr.ToString())}");
        }
    }

    private static void ParseProgressLine(
        string line,
        double totalDurationSeconds,
        IProgress<VideoEnhanceProgressReport> progress,
        string stepName)
    {
        const string key = "out_time_us=";
        if (!line.StartsWith(key, StringComparison.Ordinal) || totalDurationSeconds <= 0)
        {
            return;
        }

        var raw = line[key.Length..].Trim();
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0)
        {
            return;
        }

        var seconds = us / 1_000_000.0;
        var p01 = Math.Clamp(seconds / totalDurationSeconds, 0, 1);
        progress.Report(new VideoEnhanceProgressReport(p01, $"{stepName}… {p01 * 100:0}%"));
    }

    private static string? ResolveFfmpegPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    private static string ExtractError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "FFmpeg failed.";
        }

        var line = stderr
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);

        return string.IsNullOrEmpty(line) ? "FFmpeg failed." : line;
    }

    private static string BuildOverlayPosition(WatermarkPosition position, int margin)
    {
        var ci = CultureInfo.InvariantCulture;
        var m = margin.ToString(ci);
        return position switch
        {
            WatermarkPosition.TopLeft => $"{m}:{m}",
            WatermarkPosition.TopCenter => $"(main_w-overlay_w)/2:{m}",
            WatermarkPosition.TopRight => $"main_w-overlay_w-{m}:{m}",
            WatermarkPosition.Center => "(main_w-overlay_w)/2:(main_h-overlay_h)/2",
            WatermarkPosition.BottomLeft => $"{m}:main_h-overlay_h-{m}",
            WatermarkPosition.BottomCenter => $"(main_w-overlay_w)/2:main_h-overlay_h-{m}",
            WatermarkPosition.BottomRight => $"main_w-overlay_w-{m}:main_h-overlay_h-{m}",
            _ => $"{m}:{m}"
        };
    }

    private static string BuildDrawtextPosition(WatermarkPosition position, int margin)
    {
        var ci = CultureInfo.InvariantCulture;
        var m = margin.ToString(ci);
        return position switch
        {
            WatermarkPosition.TopLeft => $"x={m}:y={m}",
            WatermarkPosition.TopCenter => $"x=(w-text_w)/2:y={m}",
            WatermarkPosition.TopRight => $"x=w-text_w-{m}:y={m}",
            WatermarkPosition.Center => "x=(w-text_w)/2:y=(h-text_h)/2",
            WatermarkPosition.BottomLeft => $"x={m}:y=h-text_h-{m}",
            WatermarkPosition.BottomCenter => $"x=(w-text_w)/2:y=h-text_h-{m}",
            WatermarkPosition.BottomRight => $"x=w-text_w-{m}:y=h-text_h-{m}",
            _ => $"x={m}:y={m}"
        };
    }

    /// <summary>Multi-step atempo chain to safely reach factors outside FFmpeg's 0.5..2.0 single-filter range.</summary>
    private static string BuildAtempoChain(double factor)
    {
        var ci = CultureInfo.InvariantCulture;
        var parts = new List<string>();
        var remaining = factor;

        while (remaining > 2.0)
        {
            parts.Add("atempo=2.0");
            remaining /= 2.0;
        }

        while (remaining < 0.5)
        {
            parts.Add("atempo=0.5");
            remaining /= 0.5;
        }

        parts.Add($"atempo={remaining.ToString("0.######", ci)}");
        return string.Join(",", parts);
    }

    /// <summary>Escape characters that have meaning inside an FFmpeg filter argument.</summary>
    private static string EscapeFilterPath(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:");

    /// <summary>Escape characters for drawtext text='…'.</summary>
    private static string EscapeDrawtextText(string text) =>
        text
            .Replace("\\", "\\\\")
            .Replace(":", "\\:")
            .Replace("'", "\\'")
            .Replace("%", "\\%");

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
}
