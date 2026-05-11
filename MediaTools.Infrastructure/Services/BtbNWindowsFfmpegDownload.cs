using System.IO.Compression;
using System.Net;
using System.Net.Http;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Downloads a current FFmpeg win64 GPL build from BtbN (includes NVENC/AMF/QSV).
/// Xabe's <c>FFmpegVersion.Full</c> points at stale Zeranoe-era binaries that are too old for newer GPUs.
/// </summary>
internal static class BtbNWindowsFfmpegDownload
{
    internal const string MarkerFileName = ".mediatools_ffmpeg_source";
    internal const string MarkerContent = "btbn-win64-gpl-latest";

    private const string ZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    internal static bool HasInstallMarker(string ffmpegDirectory)
    {
        var markerPath = Path.Combine(ffmpegDirectory, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(markerPath);
            return text.Contains(MarkerContent, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static async Task InstallAsync(string ffmpegDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ffmpegDirectory);

        var tempZip = Path.Combine(Path.GetTempPath(), $"mediatools_ffmpeg_{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"mediatools_ffmpeg_x_{Guid.NewGuid():N}");

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(ffmpegDirectory))
            {
                try
                {
                    if (File.Exists(entry))
                    {
                        File.Delete(entry);
                    }
                    else
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                }
                catch
                {
                    // continue best-effort cleanup
                }
            }

            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(12)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd("MediaTools/1.0 (FFmpeg bootstrap)");

            await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(tempZip, tempDir);

            var ffmpeg = Directory.EnumerateFiles(tempDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            var ffprobe = Directory.EnumerateFiles(tempDir, "ffprobe.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (ffmpeg is null || ffprobe is null)
            {
                throw new InvalidOperationException(
                    "Downloaded FFmpeg archive did not contain ffmpeg.exe / ffprobe.exe. Try again or check your network.");
            }

            var destFf = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
            var destFp = Path.Combine(ffmpegDirectory, "ffprobe.exe");
            File.Copy(ffmpeg, destFf, overwrite: true);
            File.Copy(ffprobe, destFp, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempZip);
            TryDeleteDirectory(tempDir);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
