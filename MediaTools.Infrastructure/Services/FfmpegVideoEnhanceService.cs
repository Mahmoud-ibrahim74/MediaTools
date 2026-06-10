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
/// watermark, speed change, reverse, color grading, crop/resize, and video-to-audio.
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
        VideoEnhancePipelineSettings pipeline,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file was not found.", sourcePath);
        }

        var steps = pipeline.Steps;
        if (steps.Count == 0)
        {
            throw new ArgumentException("Pipeline must contain at least one step.", nameof(pipeline));
        }

        foreach (var s in steps)
        {
            if (s.Operation is VideoEnhanceOperation.ExtractAudio or VideoEnhanceOperation.ExtractSubtitle)
            {
                throw new InvalidOperationException("Audio or subtitle extraction cannot be combined in a video pipeline.");
            }
        }

        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        EnsureParentDirectoryExists(outputPath);

        var enc = pipeline.VideoEncoder;

        if (steps.Count == 1)
        {
            var settings = steps[0] with { VideoEncoder = enc };
            var analysis = await AnalyzeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            await ApplySingleEnhanceStepAsync(sourcePath, outputPath, analysis, settings, progress, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new VideoEnhanceProgressReport(1, "Done"));
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "MediaTools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var currentPath = sourcePath;
            var analysis = await AnalyzeAsync(currentPath, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = steps[i] with { VideoEncoder = enc };
                var isLast = i == steps.Count - 1;
                // MP4 + explicit muxer avoids Matroska/HW-encoder quirks that often surface as AVERROR(EINVAL) (-22).
                var nextPath = isLast ? outputPath : Path.Combine(tempRoot, $"step_{i:00}.mp4");

                var stepIndex = i;
                var stepCount = steps.Count;
                var wrapped = new Progress<VideoEnhanceProgressReport>(r =>
                {
                    var basePct = stepIndex / (double)stepCount;
                    var slice = r.Percent01 / stepCount;
                    progress.Report(new VideoEnhanceProgressReport(
                        Math.Min(1.0, basePct + slice),
                        $"Step {stepIndex + 1}/{stepCount}: {r.StepDescription}"));
                });

                await ApplySingleEnhanceStepAsync(currentPath, nextPath, analysis, step, wrapped, cancellationToken)
                    .ConfigureAwait(false);

                if (!isLast)
                {
                    if (i > 0 && currentPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(currentPath);
                    }

                    currentPath = nextPath;
                    analysis = await AnalyzeAsync(currentPath, cancellationToken).ConfigureAwait(false);
                }
            }

            progress.Report(new VideoEnhanceProgressReport(1, "Done"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    foreach (var f in Directory.EnumerateFiles(tempRoot))
                    {
                        TryDelete(f);
                    }

                    Directory.Delete(tempRoot);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private async Task ApplySingleEnhanceStepAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoEnhanceSettings settings,
        IProgress<VideoEnhanceProgressReport> progress,
        CancellationToken cancellationToken)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        EnsureParentDirectoryExists(outputPath);

        var totalDurationSeconds = analysis.Duration.TotalSeconds;
        var enc = settings.VideoEncoder;

        switch (settings.Operation)
        {
            case VideoEnhanceOperation.Watermark:
                await ApplyWatermarkAsync(sourcePath, outputPath, analysis, settings.Watermark!, enc, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.SpeedChange:
                await ApplySpeedAsync(sourcePath, outputPath, analysis, settings.Speed!, enc, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.Reverse:
                await ApplyReverseAsync(sourcePath, outputPath, analysis, enc, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.ColorGrading:
                await ApplyColorGradingAsync(sourcePath, outputPath, analysis, settings.ColorGrading!, enc, totalDurationSeconds, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VideoEnhanceOperation.CropAndResize:
                await ApplyCropResizeAsync(sourcePath, outputPath, analysis, settings.CropResize!, enc, totalDurationSeconds, progress, cancellationToken)
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

                    sb.Append(ci, $"-i \"{Path.GetFullPath(wm.ImagePath)}\" ");
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
                    var fontPath = TryResolveDrawtextFontFile();
                    if (fontPath is null && OperatingSystem.IsWindows())
                    {
                        return null;
                    }

                    filterComplex =
                        $"[0:v]drawtext=text='{EscapeDrawtextText(rawText)}':" +
                        $"{FormatDrawtextFontFileFilterOption(fontPath)}" +
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
            var err = ExtractError(stderr.ToString());
            var argsPreview = arguments.Length > 600 ? arguments[..600] + "…" : arguments;
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}: {err} | Args (truncated): {argsPreview}");
        }

        var bytes = ms.ToArray();
        if (bytes.Length < 64)
        {
            throw new InvalidOperationException("Preview output was empty.");
        }

        return bytes;
    }

    /// <summary>Pipeline chunk files step_XX.* — never use +faststart (fragile for chained re-encodes).</summary>
    private static bool IsPipelineIntermediateStepFile(string outputPath)
    {
        var name = Path.GetFileName(outputPath);
        if (!name.StartsWith("step_", StringComparison.Ordinal))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length == 7
            && char.IsDigit(stem[5])
            && char.IsDigit(stem[6]);
    }

    /// <summary>movflags +faststart is only for final ISO-BMFF deliverables, not Matroska or pipeline chunks.</summary>
    private static bool WantsMuxFaststart(string outputPath) =>
        !IsPipelineIntermediateStepFile(outputPath)
        && (Path.GetExtension(outputPath) is var ext
            && (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)));

    private static void EnsureParentDirectoryExists(string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Forces muxer so FFmpeg does not mis-detect container (avoids EINVAL opening output on Windows).</summary>
    private static void AppendMuxerFormatBeforeOutput(StringBuilder sb, string outputPath)
    {
        var ext = Path.GetExtension(outputPath);
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f mp4 ");
        }
        else if (ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f mov ");
        }
        else if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f matroska ");
        }
        else if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f mp3 ");
        }
        else if (ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f ipod ");
        }
        else if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f flac ");
        }
        else if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f ogg ");
        }
        else if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f wav ");
        }
    }

    private static void AppendQuotedOutput(StringBuilder sb, CultureInfo ci, string outputPath)
    {
        AppendMuxerFormatBeforeOutput(sb, outputPath);
        sb.Append(ci, $"\"{outputPath}\"");
    }

    private static void AppendMovFlagsFaststartIfCompatible(StringBuilder sb, string outputPath)
    {
        if (WantsMuxFaststart(outputPath))
        {
            sb.Append("-movflags +faststart ");
        }
    }

    private static void AppendH264VideoEncode(StringBuilder sb, VideoHardwareEncoderKind encoder, string outputPath, bool requestFaststart)
    {
        switch (encoder)
        {
            case VideoHardwareEncoderKind.Nvenc:
                sb.Append("-c:v h264_nvenc -preset p4 -cq 23 -pix_fmt yuv420p ");
                break;
            case VideoHardwareEncoderKind.Amf:
                sb.Append("-c:v h264_amf -quality balanced -rc cqp -qp_i 23 -qp_p 23 -pix_fmt yuv420p ");
                break;
            case VideoHardwareEncoderKind.QuickSync:
                sb.Append("-c:v h264_qsv -preset medium -global_quality 23 -pix_fmt yuv420p ");
                break;
            default:
                sb.Append("-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p ");
                break;
        }

        if (requestFaststart && WantsMuxFaststart(outputPath))
        {
            sb.Append("-movflags +faststart ");
        }
    }

    private async Task ApplyWatermarkAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoWatermarkSettings wm,
        VideoHardwareEncoderKind videoEncoder,
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

            sb.Append(ci, $"-i \"{Path.GetFullPath(wm.ImagePath)}\" ");

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
            var fontPath = TryResolveDrawtextFontFile();
            if (fontPath is null && OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "Text watermark needs a TrueType font, but none were found in Windows\\Fonts (e.g. segoeui.ttf, arial.ttf). " +
                    "Use an image watermark or install a standard UI font.");
            }

            filterComplex =
                $"[0:v]drawtext=text='{EscapeDrawtextText(rawText)}':" +
                $"{FormatDrawtextFontFileFilterOption(fontPath)}" +
                $"fontcolor=white@{opacity.ToString("0.###", ci)}:" +
                $"fontsize={fontSize.ToString(ci)}:" +
                $"box=1:boxcolor=black@{(opacity * 0.5).ToString("0.###", ci)}:" +
                $"boxborderw=10:" +
                $"{pos}[v]";
        }

        sb.Append(ci, $"-filter_complex \"{filterComplex}\" ");
        sb.Append("-map \"[v]\" ");
        sb.Append(mapAudio);
        AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: true);
        AppendQuotedOutput(sb, ci, outputPath);

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Embedding watermark", ct).ConfigureAwait(false);
    }

    private async Task ApplySpeedAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoSpeedSettings speed,
        VideoHardwareEncoderKind videoEncoder,
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
            AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: false);
            sb.Append("-c:a aac -b:a 192k ");
            AppendMovFlagsFaststartIfCompatible(sb, outputPath);
        }
        else
        {
            sb.Append(ci, $"-vf \"{videoFilter}\" ");
            AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: false);
            sb.Append("-an ");
            AppendMovFlagsFaststartIfCompatible(sb, outputPath);
        }

        AppendQuotedOutput(sb, ci, outputPath);
        var stepName = $"Speed {factor.ToString("0.##", ci)}x";

        // Output duration shrinks by factor when speeding up; total progress = output_time / output_duration
        var outputDuration = totalDurationSeconds > 0 ? totalDurationSeconds / factor : 0;
        await RunFfmpegAsync(sb.ToString(), outputDuration, progress, stepName, ct).ConfigureAwait(false);
    }

    private async Task ApplyReverseAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoHardwareEncoderKind videoEncoder,
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
            AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: false);
            sb.Append("-c:a aac -b:a 192k ");
        }
        else
        {
            sb.Append("-vf reverse -an ");
            AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: false);
        }

        AppendMovFlagsFaststartIfCompatible(sb, outputPath);
        AppendQuotedOutput(sb, ci, outputPath);

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Reversing", ct).ConfigureAwait(false);
    }

    private async Task ApplyColorGradingAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoColorGradingSettings color,
        VideoHardwareEncoderKind videoEncoder,
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

        AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: true);
        AppendQuotedOutput(sb, ci, outputPath);

        await RunFfmpegAsync(sb.ToString(), totalDurationSeconds, progress, "Color grading", ct).ConfigureAwait(false);
    }

    private async Task ApplyCropResizeAsync(
        string sourcePath,
        string outputPath,
        VideoSourceAnalysis analysis,
        VideoCropResizeSettings cr,
        VideoHardwareEncoderKind videoEncoder,
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

        AppendH264VideoEncode(sb, videoEncoder, outputPath, requestFaststart: true);
        AppendQuotedOutput(sb, ci, outputPath);

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

        AppendQuotedOutput(sb, ci, outputPath);

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
            var err = ExtractError(stderr.ToString());
            var argsPreview = arguments.Length > 600 ? arguments[..600] + "…" : arguments;
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}: {err} | Args (truncated): {argsPreview}");
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
        var path = ToolPaths.FfmpegExePath;
        return File.Exists(path) ? path : null;
    }

    private static string ExtractError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "FFmpeg failed.";
        }

        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .TakeLast(8)
            .ToArray();

        return lines.Length == 0 ? "FFmpeg failed." : string.Join(" | ", lines);
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

    /// <summary>
    /// Text watermarks use drawtext. Without <c>fontfile=</c>, many Windows FFmpeg builds call Fontconfig with no
    /// config and crash (access violation) or fail. A concrete .ttf path avoids Fontconfig.
    /// </summary>
    private static string? TryResolveDrawtextFontFile()
    {
        if (OperatingSystem.IsWindows())
        {
            var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            ReadOnlySpan<string> names =
            [
                "segoeui.ttf",
                "arial.ttf",
                "calibri.ttf",
                "SegoeUI.ttf"
            ];

            foreach (var name in names)
            {
                var p = Path.Combine(fonts, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            ReadOnlySpan<string> mac =
            [
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/Library/Fonts/Arial.ttf",
                "/System/Library/Fonts/Helvetica.ttc"
            ];

            foreach (var p in mac)
            {
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return null;
        }

        ReadOnlySpan<string> linux =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf"
        ];

        foreach (var p in linux)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Drive colons and special characters escaped for use in a drawtext filter option string.</summary>
    private static string EscapeDrawtextFontPathForFilter(string absolutePath)
    {
        var normalized = Path.GetFullPath(absolutePath).Replace('\\', '/');
        if (normalized.Length >= 2
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':')
        {
            normalized = $"{normalized[0]}\\:{normalized[2..]}";
        }

        return normalized.Replace("'", "\\'", StringComparison.Ordinal);
    }

    /// <summary>Returns <c>fontfile=…:</c> or empty when no font (non-Windows: fall back to FFmpeg default / fontconfig).</summary>
    private static string FormatDrawtextFontFileFilterOption(string? fontPath) =>
        string.IsNullOrWhiteSpace(fontPath)
            ? string.Empty
            : $"fontfile={EscapeDrawtextFontPathForFilter(fontPath)}:";

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
