using System.Buffers.Binary;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Entities;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MediaTools.Infrastructure.Services;

public sealed class ImageSharpPhotoProcessingService : IImageProcessingService
{
    private const int MaxDimension = 16000;
    private const int PreviewWorkspaceMaxEdge = 1600;
    private const int PreviewOutputMaxEdge = 720;

    public async Task<RasterImageFile> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file was not found.", filePath);
        }

        var info = await Image.IdentifyAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            throw new InvalidOperationException("Could not read image metadata.");
        }

        var fileName = Path.GetFileName(filePath);
        var len = new FileInfo(filePath).Length;
        var fmt = info.Metadata.DecodedImageFormat?.Name ?? Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        return RasterImageFile.Create(filePath, fileName, len, info.Width, info.Height, fmt);
    }

    public async Task ProcessAsync(
        string sourcePath,
        string outputPath,
        PhotoEnhanceSettings settings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new PhotoProgressReport(0.02, "Loading image…"));

        await using var fs = File.OpenRead(sourcePath);
        using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(0.15, "Applying resize & enhancement…"));

        ApplyFullPipeline(image, settings);

        progress?.Report(new PhotoProgressReport(0.75, "Encoding…"));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        await SaveAsync(image, outputPath, settings, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(1, "Done"));
    }

    public async Task<byte[]?> GetEditedPreviewPngAsync(
        string sourcePath,
        PhotoEnhanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            await using var fs = File.OpenRead(sourcePath);
            using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);

            DownscaleLongEdgeIfNeeded(image, PreviewWorkspaceMaxEdge);

            ApplyFullPipeline(image, settings);

            DownscaleLongEdgeIfNeeded(image, PreviewOutputMaxEdge);

            await using var ms = new MemoryStream();
            await image.SaveAsPngAsync(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed }, cancellationToken)
                .ConfigureAwait(false);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyFullPipeline(Image<Rgba32> image, PhotoEnhanceSettings settings)
    {
        ApplyResize(image, settings);

        if (settings.UpscaleMode == UpscaleQualityMode.AiEnhanced)
        {
            ApplyAiStyleEnhancement(image);
        }

        ApplyFilter(image, settings.Filter);
    }

    private static void DownscaleLongEdgeIfNeeded(Image<Rgba32> image, int maxEdge)
    {
        var longSide = Math.Max(image.Width, image.Height);
        if (longSide <= maxEdge)
        {
            return;
        }

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

    private static void ApplyResize(Image<Rgba32> image, PhotoEnhanceSettings settings)
    {
        var (tw, th) = ComputeTargetSize(image.Width, image.Height, settings);
        if (tw == image.Width && th == image.Height)
        {
            return;
        }

        var sampler = settings.UpscaleMode == UpscaleQualityMode.None
            ? KnownResamplers.Bicubic
            : KnownResamplers.Lanczos3;

        image.Mutate(ctx =>
        {
            ctx.Resize(new ResizeOptions
            {
                Size = new Size(tw, th),
                Mode = ResizeMode.Stretch,
                Sampler = sampler
            });
        });
    }

    private static (int Width, int Height) ComputeTargetSize(int w, int h, PhotoEnhanceSettings settings)
    {
        switch (settings.ResizeIntent)
        {
            case PhotoResizeIntent.Original:
                return (w, h);

            case PhotoResizeIntent.ScaleByFactor:
            {
                var f = Math.Clamp(settings.ScaleFactor, 0.05, 8);
                var nw = (int)Math.Round(w * f);
                var nh = (int)Math.Round(h * f);
                return (ClampDim(nw), ClampDim(nh));
            }

            case PhotoResizeIntent.FitMaxEdge:
            {
                if (settings.MaxEdgePixels is not { } maxEdge || maxEdge < 32)
                {
                    return (w, h);
                }

                var longSide = Math.Max(w, h);
                var scale = (double)maxEdge / longSide;
                var nw = (int)Math.Round(w * scale);
                var nh = (int)Math.Round(h * scale);
                return (ClampDim(nw), ClampDim(nh));
            }

            default:
                return (w, h);
        }
    }

    private static int ClampDim(int v) => Math.Clamp(v, 1, MaxDimension);

    private static void ApplyAiStyleEnhancement(Image<Rgba32> image)
    {
        image.Mutate(ctx =>
        {
            ctx.GaussianSharpen(0.65f);
            ctx.Contrast(1.04f);
            ctx.Brightness(1.015f);
            ctx.Saturate(1.03f);
        });
    }

    private static void ApplyFilter(Image<Rgba32> image, PhotoFilterKind filter)
    {
        if (filter == PhotoFilterKind.None)
        {
            return;
        }

        image.Mutate(ctx =>
        {
            switch (filter)
            {
                case PhotoFilterKind.Grayscale:
                    ctx.Grayscale();
                    break;
                case PhotoFilterKind.Sepia:
                    ctx.Sepia();
                    break;
                case PhotoFilterKind.Vintage:
                    ctx.Sepia();
                    ctx.Saturate(0.85f);
                    ctx.Brightness(1.03f);
                    ctx.Contrast(0.95f);
                    break;
                case PhotoFilterKind.Sharpen:
                    ctx.GaussianSharpen(1.1f);
                    break;
                case PhotoFilterKind.SoftBlur:
                    ctx.GaussianBlur(0.8f);
                    break;
                case PhotoFilterKind.WarmGlow:
                    ctx.Hue(6f);
                    ctx.Saturate(1.08f);
                    ctx.Brightness(1.02f);
                    break;
                case PhotoFilterKind.CoolMist:
                    ctx.Hue(-8f);
                    ctx.Saturate(0.94f);
                    ctx.Brightness(1.01f);
                    break;
                case PhotoFilterKind.DramaticContrast:
                    ctx.Contrast(1.18f);
                    ctx.Saturate(1.06f);
                    ctx.Brightness(0.98f);
                    break;
            }
        });
    }

    private static async Task SaveAsync(
        Image<Rgba32> image,
        string outputPath,
        PhotoEnhanceSettings settings,
        CancellationToken cancellationToken)
    {
        var q = Math.Clamp(settings.EncodingQuality, 1, 100);

        switch (settings.TargetFormat)
        {
            case RasterImageFormat.Png:
                await image.SaveAsPngAsync(outputPath, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression }, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case RasterImageFormat.Jpeg:
                await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = q }, cancellationToken).ConfigureAwait(false);
                break;

            case RasterImageFormat.Webp:
                await image.SaveAsWebpAsync(outputPath, new WebpEncoder { Quality = q }, cancellationToken).ConfigureAwait(false);
                break;

            case RasterImageFormat.Bmp:
                await image.SaveAsBmpAsync(outputPath, cancellationToken).ConfigureAwait(false);
                break;

            case RasterImageFormat.Tiff:
                await image.SaveAsTiffAsync(outputPath, cancellationToken).ConfigureAwait(false);
                break;

            case RasterImageFormat.Ico:
                await SaveAsIcoWithEmbeddedPngAsync(image, outputPath, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(settings), settings.TargetFormat, null);
        }
    }

    /// <summary>
    /// Writes a single-size ICO using PNG payload (supported on Windows Vista+). Longest edge is capped at 256 px for broad compatibility.
    /// </summary>
    private static async Task SaveAsIcoWithEmbeddedPngAsync(
        Image<Rgba32> image,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var frame = image.Clone();
        var longSide = Math.Max(frame.Width, frame.Height);
        if (longSide > 256)
        {
            var scale = 256.0 / longSide;
            var nw = Math.Max(1, (int)Math.Round(frame.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(frame.Height * scale));
            frame.Mutate(ctx =>
            {
                ctx.Resize(new ResizeOptions
                {
                    Size = new Size(nw, nh),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                });
            });
        }

        await using var pngMs = new MemoryStream();
        await frame.SaveAsPngAsync(
                pngMs,
                new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression },
                cancellationToken)
            .ConfigureAwait(false);
        var pngBytes = pngMs.ToArray();

        await using var outStream = File.Create(outputPath);
        await WriteIcoContainerWithPngImageAsync(outStream, frame.Width, frame.Height, pngBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteIcoContainerWithPngImageAsync(
        Stream destination,
        int width,
        int height,
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        var w = (byte)(width >= 256 ? 0 : width);
        var h = (byte)(height >= 256 ? 0 : height);
        var header = new byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), 1);
        header[6] = w;
        header[7] = h;
        header[8] = 0;
        header[9] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), (uint)pngBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), 22);

        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(pngBytes, cancellationToken).ConfigureAwait(false);
    }
}
