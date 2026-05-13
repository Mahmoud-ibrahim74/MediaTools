using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MediaTools.Infrastructure.Services;

public sealed partial class ImageSharpPhotoProcessingService
{
    private const int MattingPreviewMaxEdge = 1600;

    public async Task<byte[]?> GetBackgroundRemovalPreviewPngAsync(
        string sourcePath,
        BackgroundRemovalSettings settings,
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
            DownscaleLongEdgeIfNeeded(image, MattingPreviewMaxEdge);
            ApplyBackgroundRemoval(image, settings);
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

    public async Task RemoveBackgroundToFileAsync(
        string sourcePath,
        string outputPath,
        BackgroundRemovalSettings settings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new PhotoProgressReport(0.05, "Loading image…"));
        await using var fs = File.OpenRead(sourcePath);
        using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(0.35, "Removing background…"));
        ApplyBackgroundRemoval(image, settings);

        progress?.Report(new PhotoProgressReport(0.75, "Saving PNG…"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await image.SaveAsPngAsync(outputPath, new PngEncoder { CompressionLevel = PngCompressionLevel.BestCompression }, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(1, "Done"));
    }

    public async Task<byte[]?> GetObjectEraserPreviewPngAsync(
        string sourcePath,
        IReadOnlyList<EraserBrushStamp> stamps,
        ObjectEraserSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath) || stamps.Count == 0)
        {
            return null;
        }

        try
        {
            await using var fs = File.OpenRead(sourcePath);
            using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);
            var ow = image.Width;
            var oh = image.Height;
            DownscaleLongEdgeIfNeeded(image, MattingPreviewMaxEdge);
            var sx = image.Width / (float)ow;
            var sy = image.Height / (float)oh;
            var sMax = Math.Max(sx, sy);
            var scaled = stamps.Select(st => new EraserBrushStamp(
                st.ImagePixelX * sx,
                st.ImagePixelY * sy,
                st.RadiusPx * sMax,
                st.Softness01)).ToList();

            ApplyObjectEraserInPlace(image, scaled, settings);
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

    public async Task ApplyObjectEraserToFileAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<EraserBrushStamp> stamps,
        ObjectEraserSettings eraserSettings,
        PhotoEnhanceSettings encodeSettings,
        IProgress<PhotoProgressReport> progress,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new PhotoProgressReport(0.05, "Loading image…"));
        await using var fs = File.OpenRead(sourcePath);
        using var image = await Image.LoadAsync<Rgba32>(fs, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(0.25, "Applying object eraser…"));
        if (stamps.Count > 0)
        {
            ApplyObjectEraserInPlace(image, stamps, eraserSettings);
        }

        progress?.Report(new PhotoProgressReport(0.72, "Encoding…"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await SaveAsync(image, outputPath, encodeSettings, cancellationToken).ConfigureAwait(false);

        progress?.Report(new PhotoProgressReport(1, "Done"));
    }

    private static void ApplyBackgroundRemoval(Image<Rgba32> image, BackgroundRemovalSettings settings)
    {
        var tol = Math.Clamp(settings.Tolerance, 0, 100);
        var feather = Math.Max(0f, settings.FeatherSigma);
        var expand = Math.Clamp(settings.EdgeExpandPx, 0, 48);

        switch (settings.Mode)
        {
            case BackgroundRemovalMode.AutoEdge:
                RemoveBackgroundAutoEdge(image, tol, feather, expand);
                break;
            case BackgroundRemovalMode.ChromaKey:
                RemoveBackgroundChroma(image, settings.KeyR, settings.KeyG, settings.KeyB, tol, feather);
                break;
            case BackgroundRemovalMode.Luminance:
                RemoveBackgroundLuminance(image, Math.Clamp(settings.LuminanceThreshold01, 0f, 1f), feather, expand);
                break;
            default:
                RemoveBackgroundAutoEdge(image, tol, feather, expand);
                break;
        }
    }

    private static void RemoveBackgroundAutoEdge(Image<Rgba32> image, int tolerance, float featherSigma, int expandPx)
    {
        var w = image.Width;
        var h = image.Height;
        var bgRef = EstimateCornerBackground(image);
        var maxDist = 18f + tolerance * 2.15f;
        var maxDistSq = maxDist * maxDist;

        var remove = new bool[w * h];
        var visited = new bool[w * h];
        var q = new Queue<(int X, int Y)>();

        bool Similar(Rgba32 p)
        {
            var dr = p.R - bgRef.R;
            var dg = p.G - bgRef.G;
            var db = p.B - bgRef.B;
            return dr * dr + dg * dg + db * db <= maxDistSq;
        }

        void TryEnqueue(int x, int y)
        {
            var i = y * w + x;
            if (visited[i])
            {
                return;
            }

            if (!Similar(image[x, y]))
            {
                return;
            }

            visited[i] = true;
            remove[i] = true;
            q.Enqueue((x, y));
        }

        for (var x = 0; x < w; x++)
        {
            TryEnqueue(x, 0);
            TryEnqueue(x, h - 1);
        }

        for (var y = 0; y < h; y++)
        {
            TryEnqueue(0, y);
            TryEnqueue(w - 1, y);
        }

        Span<(int dx, int dy)> dirs = stackalloc (int dx, int dy)[]
        {
            (-1, 0), (1, 0), (0, -1), (0, 1)
        };

        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in dirs)
            {
                var nx = x + dx;
                var ny = y + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                {
                    continue;
                }

                TryEnqueue(nx, ny);
            }
        }

        if (expandPx > 0)
        {
            DilateRemoval(remove, w, h, expandPx);
        }

        ApplyRemovalMaskWithFeather(image, remove, featherSigma);
    }

    private static void RemoveBackgroundChroma(Image<Rgba32> image, byte kr, byte kg, byte kb, int tolerance, float featherSigma)
    {
        var w = image.Width;
        var h = image.Height;
        var maxDist = 22f + tolerance * 2.4f;
        var maxDistSq = maxDist * maxDist;
        var remove = new bool[w * h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = image[x, y];
                var dr = p.R - kr;
                var dg = p.G - kg;
                var db = p.B - kb;
                remove[y * w + x] = dr * dr + dg * dg + db * db <= maxDistSq;
            }
        }

        ApplyRemovalMaskWithFeather(image, remove, featherSigma);
    }

    private static void RemoveBackgroundLuminance(Image<Rgba32> image, float threshold01, float featherSigma, int expandPx)
    {
        var w = image.Width;
        var h = image.Height;
        var threshold = threshold01 * 255f;
        var remove = new bool[w * h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = image[x, y];
                var lum = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
                remove[y * w + x] = lum >= threshold;
            }
        }

        if (expandPx > 0)
        {
            DilateRemoval(remove, w, h, expandPx);
        }

        ApplyRemovalMaskWithFeather(image, remove, featherSigma);
    }

    private static void DilateRemoval(bool[] remove, int w, int h, int iterations)
    {
        var tmp = new bool[remove.Length];
        for (var it = 0; it < iterations; it++)
        {
            Array.Copy(remove, tmp, remove.Length);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (tmp[i])
                    {
                        continue;
                    }

                    var any = false;
                    for (var oy = -1; oy <= 1 && !any; oy++)
                    {
                        for (var ox = -1; ox <= 1 && !any; ox++)
                        {
                            var nx = x + ox;
                            var ny = y + oy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
                            {
                                continue;
                            }

                            if (tmp[ny * w + nx])
                            {
                                any = true;
                            }
                        }
                    }

                    if (any)
                    {
                        remove[i] = true;
                    }
                }
            }
        }
    }

    private static void ApplyRemovalMaskWithFeather(Image<Rgba32> image, bool[] remove, float featherSigma)
    {
        var w = image.Width;
        var h = image.Height;

        using var maskImg = new Image<L8>(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                maskImg[x, y] = remove[y * w + x] ? new L8(255) : new L8(0);
            }
        }

        if (featherSigma > 0.05f)
        {
            maskImg.Mutate(ctx => ctx.GaussianBlur(featherSigma));
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var m = maskImg[x, y].PackedValue / 255f;
                var p = image[x, y];
                var na = (byte)Math.Clamp((int)Math.Round(p.A * (1f - m)), 0, 255);
                image[x, y] = new Rgba32(p.R, p.G, p.B, na);
            }
        }
    }

    private static Rgba32 EstimateCornerBackground(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        var span = Math.Clamp(Math.Min(w, h) / 14, 3, 24);
        long r = 0, g = 0, b = 0;
        var n = 0;

        void SampleCorner(int cx, int cy)
        {
            for (var dy = 0; dy < span; dy++)
            {
                for (var dx = 0; dx < span; dx++)
                {
                    var x = Math.Clamp(cx + dx, 0, w - 1);
                    var y = Math.Clamp(cy + dy, 0, h - 1);
                    var p = image[x, y];
                    r += p.R;
                    g += p.G;
                    b += p.B;
                    n++;
                }
            }
        }

        SampleCorner(0, 0);
        SampleCorner(w - span, 0);
        SampleCorner(0, h - span);
        SampleCorner(w - span, h - span);

        if (n == 0)
        {
            return new Rgba32(255, 255, 255, 255);
        }

        return new Rgba32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
    }

    private static void ApplyObjectEraserInPlace(Image<Rgba32> image, IReadOnlyList<EraserBrushStamp> stamps, ObjectEraserSettings settings)
    {
        var w = image.Width;
        var h = image.Height;
        var mask = new float[w * h];

        foreach (var st in stamps)
        {
            var cx = st.ImagePixelX;
            var cy = st.ImagePixelY;
            var radius = Math.Max(1f, st.RadiusPx);
            var softness = Math.Clamp(st.Softness01, 0f, 1f);
            var inner = radius * (1f - softness * 0.92f);
            var outer = radius;

            var minX = Math.Clamp((int)Math.Floor(cx - outer - 3), 0, w - 1);
            var maxX = Math.Clamp((int)Math.Ceiling(cx + outer + 3), 0, w - 1);
            var minY = Math.Clamp((int)Math.Floor(cy - outer - 3), 0, h - 1);
            var maxY = Math.Clamp((int)Math.Ceiling(cy + outer + 3), 0, h - 1);

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var dx = x + 0.5f - cx;
                    var dy = y + 0.5f - cy;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    float contrib;
                    if (dist <= inner)
                    {
                        contrib = 1f;
                    }
                    else if (dist >= outer)
                    {
                        contrib = 0f;
                    }
                    else
                    {
                        contrib = 1f - (dist - inner) / (outer - inner + 0.0001f);
                    }

                    var idx = y * w + x;
                    if (contrib > mask[idx])
                    {
                        mask[idx] = contrib;
                    }
                }
            }
        }

        var sigma = Math.Clamp(settings.InpaintBlurSigma, 0.3f, 80f);
        using var blurred = image.Clone(ctx => ctx.GaussianBlur(sigma));

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var m = mask[y * w + x];
                if (m <= 0.001f)
                {
                    continue;
                }

                var o = image[x, y];
                var b = blurred[x, y];
                image[x, y] = new Rgba32(
                    (byte)Math.Clamp((int)Math.Round(o.R * (1 - m) + b.R * m), 0, 255),
                    (byte)Math.Clamp((int)Math.Round(o.G * (1 - m) + b.G * m), 0, 255),
                    (byte)Math.Clamp((int)Math.Round(o.B * (1 - m) + b.B * m), 0, 255),
                    o.A);
            }
        }
    }
}
