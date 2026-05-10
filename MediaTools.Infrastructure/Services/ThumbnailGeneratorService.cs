using System.Globalization;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xabe.FFmpeg;

namespace MediaTools.Infrastructure.Services;

public sealed class ThumbnailGeneratorService(
    IVideoCompressionService videoCompressionService,
    IImageProcessingService imageProcessingService) : IThumbnailGeneratorService
{
    private static readonly HashSet<string> ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    ];

    public async Task<ThumbnailSourceAnalysis> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File was not found.", filePath);
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
        {
            var r = await imageProcessingService.AnalyzeAsync(filePath, cancellationToken).ConfigureAwait(false);
            return new ThumbnailSourceAnalysis(
                filePath,
                r.FileName,
                r.FileSizeBytes,
                IsVideo: false,
                Duration: null,
                MediaWidth: r.Width,
                MediaHeight: r.Height,
                r.FormatHint);
        }

        var info = await FFmpeg.GetMediaInfo(filePath, cancellationToken).ConfigureAwait(false);
        var v = info.VideoStreams.FirstOrDefault();
        if (v is null)
        {
            throw new InvalidOperationException("No video stream found. Use a video file or a raster image.");
        }

        var sizeBytes = info.Size > 0 ? info.Size : new FileInfo(filePath).Length;
        var duration = info.Duration > TimeSpan.Zero ? info.Duration : TimeSpan.Zero;

        return new ThumbnailSourceAnalysis(
            filePath,
            Path.GetFileName(filePath),
            sizeBytes,
            IsVideo: true,
            duration > TimeSpan.Zero ? duration : null,
            v.Width,
            v.Height,
            v.Codec);
    }

    public async Task GenerateAsync(
        string sourcePath,
        string outputPath,
        ThumbnailGeneratorSettings settings,
        IProgress<ThumbnailProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        await videoCompressionService.EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
        {
            await GenerateFromImageAsync(sourcePath, outputPath, settings, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await GenerateFromVideoAsync(sourcePath, outputPath, settings, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task GenerateFromImageAsync(
        string sourcePath,
        string outputPath,
        ThumbnailGeneratorSettings settings,
        IProgress<ThumbnailProgressReport> progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ThumbnailProgressReport(0.1, "Loading image…"));

        await using var fs = File.OpenRead(sourcePath);
        using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);

        var maxEdge = Math.Clamp(settings.MaxEdgePixels, 32, 8192);
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide > maxEdge)
        {
            var scale = (double)maxEdge / longSide;
            var nw = Math.Max(1, (int)Math.Round(image.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(ctx =>
            {
                ctx.Resize(new ResizeOptions
                {
                    Size = new Size(nw, nh),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                });
            });
        }

        progress?.Report(new ThumbnailProgressReport(0.55, "Saving…"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var q = Math.Clamp(settings.JpegWebpQuality, 1, 100);

        switch (settings.OutputFormat)
        {
            case ThumbnailOutputFormat.Jpeg:
                await image.SaveAsJpegAsync(
                        outputPath,
                        new JpegEncoder { Quality = q },
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ThumbnailOutputFormat.Png:
                await image.SaveAsPngAsync(
                        outputPath,
                        new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression },
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ThumbnailOutputFormat.Webp:
                await image.SaveAsWebpAsync(
                        outputPath,
                        new WebpEncoder { Quality = q },
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.OutputFormat, null);
        }

        progress?.Report(new ThumbnailProgressReport(1, "Done"));
    }

    private static async Task GenerateFromVideoAsync(
        string sourcePath,
        string outputPath,
        ThumbnailGeneratorSettings settings,
        IProgress<ThumbnailProgressReport> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var box = Math.Clamp(settings.MaxEdgePixels, 32, 8192);
        var t = Math.Max(0, settings.VideoTimeOffsetSeconds);

        var conversion = FFmpeg.Conversions.New();
        conversion.SetOverwriteOutput(true);
        conversion.AddParameter($"-ss {t.ToString(CultureInfo.InvariantCulture)}", ParameterPosition.PreInput);
        conversion.AddParameter($"-i \"{sourcePath}\"", ParameterPosition.PostInput);
        conversion.AddParameter("-vframes 1", ParameterPosition.PostInput);
        conversion.AddParameter(
            $"-vf scale={box}:{box}:force_original_aspect_ratio=decrease",
            ParameterPosition.PostInput);

        switch (settings.OutputFormat)
        {
            case ThumbnailOutputFormat.Jpeg:
            {
                var qv = MapJpegQualityToFfmpegQv(settings.JpegWebpQuality);
                conversion.AddParameter($"-q:v {qv}", ParameterPosition.PostInput);
                break;
            }
            case ThumbnailOutputFormat.Png:
                break;
            case ThumbnailOutputFormat.Webp:
                conversion.AddParameter("-c:v libwebp", ParameterPosition.PostInput);
                conversion.AddParameter(
                    $"-quality {Math.Clamp(settings.JpegWebpQuality, 1, 100)}",
                    ParameterPosition.PostInput);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.OutputFormat, null);
        }

        conversion.SetOutput(outputPath);

        conversion.OnProgress += (_, args) =>
        {
            var p01 = Math.Clamp(args.Percent / 100.0, 0, 1);
            progress?.Report(new ThumbnailProgressReport(p01, $"Extracting frame… {args.Percent:0}%"));
        };

        progress?.Report(new ThumbnailProgressReport(0, "Extracting frame…"));
        await conversion.Start(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("Thumbnail file was not created.");
        }

        progress?.Report(new ThumbnailProgressReport(1, "Done"));
    }

    private static int MapJpegQualityToFfmpegQv(int quality)
    {
        var q = Math.Clamp(quality, 1, 100);
        return Math.Clamp((int)Math.Round(31 - (q - 1) * 29 / 99.0), 2, 31);
    }
}
