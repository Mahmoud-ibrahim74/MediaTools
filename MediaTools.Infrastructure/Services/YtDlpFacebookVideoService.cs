using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Downloads videos and reels from Facebook using yt-dlp and
/// leverages FFmpeg to merge separate DASH video and audio streams into an MP4/MKV/WebM container.
/// </summary>
public sealed partial class YtDlpFacebookVideoService : IFacebookVideoService
{
    public async Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default)
    {
        await YtDlpProcessHelper.EnsureYtDlpReadyAndUpdatedAsync(cancellationToken).ConfigureAwait(false);

        if (!ToolPaths.IsYtDlpReady)
        {
            throw new InvalidOperationException(
                "yt-dlp is not installed. Restart the application to trigger the download.");
        }

        if (!ToolPaths.IsFfmpegReady)
        {
            throw new InvalidOperationException(
                "FFmpeg is not installed. FFmpeg is required to merge Facebook DASH video and audio streams.");
        }
    }

    public async Task<FacebookVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!IsValidFacebookUrl(url))
        {
            throw new ArgumentException("The provided URL is not a recognized Facebook video, reel, or watch link.");
        }

        return await Task.Run(async () =>
        {
            var rawArgs = $"--dump-single-json --no-download --no-playlist \"{url.Trim()}\"";
            var args = YtDlpProcessHelper.EnhanceArguments(rawArgs);

            var psi = new ProcessStartInfo
            {
                FileName = ToolPaths.YtDlpExePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            YtDlpProcessHelper.ConfigureProcessStartInfo(psi);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                throw ClassifyError(proc.ExitCode, stderr);
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Facebook Video" : "Facebook Video";
            var author = root.TryGetProperty("uploader", out var upProp) ? upProp.GetString() ?? "" :
                         root.TryGetProperty("channel", out var chProp) ? chProp.GetString() ?? "" :
                         root.TryGetProperty("creator", out var crProp) ? crProp.GetString() ?? "" : "Facebook";

            string? thumbnail = null;
            if (root.TryGetProperty("thumbnail", out var thProp))
            {
                thumbnail = thProp.GetString();
            }

            if (string.IsNullOrEmpty(thumbnail) && root.TryGetProperty("thumbnails", out var thsProp) &&
                thsProp.ValueKind == JsonValueKind.Array && thsProp.GetArrayLength() > 0)
            {
                var lastTh = thsProp[thsProp.GetArrayLength() - 1];
                if (lastTh.TryGetProperty("url", out var urlProp))
                {
                    thumbnail = urlProp.GetString();
                }
            }

            var durationSec = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : 0;
            var duration = TimeSpan.FromSeconds(durationSec);

            var viewCount = root.TryGetProperty("view_count", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt64() : 0;

            return new FacebookVideoInfo(
                Title: title,
                AuthorName: author,
                Duration: duration,
                ThumbnailUrl: thumbnail,
                ViewCount: viewCount);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadVideoAsync(
        FacebookVideoDownloadRequest request,
        IProgress<FacebookDownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        if (!IsValidFacebookUrl(request.Url))
        {
            throw new ArgumentException("The provided URL is not a recognized Facebook video, reel, or watch link.");
        }

        Directory.CreateDirectory(request.OutputFolderPath);

        var formatExt = MapFormatToExtension(request.VideoFormat);
        var sortFilter = BuildSortFilter(request.Resolution, request.VideoQuality);
        // Limit title length to 80 chars to avoid Windows MAX_PATH violations
        var outputTemplate = Path.Combine(request.OutputFolderPath, "%(title).80s.%(ext)s");

        // Format selector: 'bv*+ba/b' fetches best video DASH stream + best audio DASH stream, and merges them
        // --windows-filenames: sanitizes illegal Windows characters and unicode directional marks (\u2068, \u2069)
        // --trim-filenames 100: ensures temporary stream part files never exceed Windows path length limits
        var args = $"-f \"bv*+ba/b\" -S \"{sortFilter}\" " +
                   $"--merge-output-format {formatExt} " +
                   $"--windows-filenames --trim-filenames 100 " +
                   $"--no-playlist --newline --no-mtime --progress --no-colors " +
                   $"--ffmpeg-location \"{ToolPaths.FfmpegDirectory}\" " +
                   $"-o \"{outputTemplate}\" \"{request.Url.Trim()}\"";

        return await Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolPaths.YtDlpExePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            YtDlpProcessHelper.ConfigureProcessStartInfo(psi);

            using var proc = new Process { StartInfo = psi };
            var outputFilePath = string.Empty;

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to launch yt-dlp: {ex.Message}", ex);
            }

            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Detect destination file path from yt-dlp output
                var destMatch = DestinationRegex().Match(line);
                if (destMatch.Success)
                {
                    outputFilePath = destMatch.Groups[1].Value.Trim();
                }

                // Detect post-processing phase (FFmpeg DASH stream merging)
                if (line.Contains("[Merger]") || line.Contains("[ffmpeg]") || line.Contains("Merging formats"))
                {
                    progress.Report(new FacebookDownloadProgress(
                        ProgressPercent: 99,
                        StatusText: "Merging DASH video & audio with FFmpeg…",
                        DownloadSpeedDisplay: null,
                        EtaDisplay: null,
                        IsMuxing: true));
                    continue;
                }

                // Parse download progress: [download]  42.5% of ~5.30MiB at  2.50MiB/s ETA 00:03
                var progressMatch = ProgressRegex().Match(line);
                if (progressMatch.Success)
                {
                    var pct = double.TryParse(progressMatch.Groups[1].Value, out var p) ? p : 0;
                    var speed = progressMatch.Groups.Count > 2 ? progressMatch.Groups[2].Value : null;
                    var eta = progressMatch.Groups.Count > 3 ? progressMatch.Groups[3].Value : null;

                    var displayPct = Math.Clamp(pct, 0, 98);

                    progress.Report(new FacebookDownloadProgress(
                        ProgressPercent: displayPct,
                        StatusText: $"Downloading stream… {pct:0.0}%",
                        DownloadSpeedDisplay: speed,
                        EtaDisplay: eta,
                        IsMuxing: false));
                }
            }

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                throw ClassifyError(proc.ExitCode, stderr);
            }

            // Fallback file location if stdout didn't capture exact name
            if (string.IsNullOrWhiteSpace(outputFilePath) || !File.Exists(outputFilePath))
            {
                outputFilePath = FindLatestFileInDirectory(request.OutputFolderPath, $"*.{formatExt}");
            }

            progress.Report(new FacebookDownloadProgress(
                ProgressPercent: 100,
                StatusText: "Download complete",
                DownloadSpeedDisplay: null,
                EtaDisplay: null,
                IsMuxing: false));

            return outputFilePath ?? request.OutputFolderPath;
        }, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsValidFacebookUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return FacebookUrlRegex().IsMatch(url.Trim());
    }

    private static void SetFfmpegEnvVars(ProcessStartInfo psi)
    {
        var currentPath = psi.Environment.TryGetValue("PATH", out var p) ? p : "";
        psi.Environment["PATH"] = ToolPaths.FfmpegDirectory + ";" + currentPath;
    }

    private static string MapFormatToExtension(FacebookVideoFormat format) =>
        format switch
        {
            FacebookVideoFormat.Mp4 => "mp4",
            FacebookVideoFormat.Mkv => "mkv",
            FacebookVideoFormat.Webm => "webm",
            _ => "mp4"
        };

    private static string BuildSortFilter(string resolution, string quality)
    {
        var resPart = resolution switch
        {
            "4K (2160p)" => "res:2160",
            "1440p" => "res:1440",
            "1080p" => "res:1080",
            "720p" => "res:720",
            "480p" => "res:480",
            _ => ""
        };

        var qualPart = quality switch
        {
            "High" => "br",
            "Low" => "+br",
            _ => ""
        };

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(resPart)) parts.Add(resPart);
        if (!string.IsNullOrEmpty(qualPart)) parts.Add(qualPart);

        return parts.Count > 0 ? string.Join(",", parts) : "res";
    }

    private static string? FindLatestFileInDirectory(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.GetFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static Exception ClassifyError(int exitCode, string stderr)
    {
        var trimmed = stderr.Trim();

        if (trimmed.Contains("Private video", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("This video may be private", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException("This Facebook video is private or requires login permissions.");
        }

        if (trimmed.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Content is not available", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException("The requested Facebook video was deleted or cannot be found.");
        }

        if (trimmed.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException("The URL is not supported or not recognized as a valid Facebook video.");
        }

        if (trimmed.Contains("unable to open for writing", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Errno 2", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("File name too long", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException("File system error: The video title or export folder path exceeded Windows filename length limits.");
        }

        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lastLine = lines.Length > 0 ? lines[^1] : "yt-dlp process failed.";

        return new InvalidOperationException($"Facebook download failed (exit {exitCode}): {lastLine}");
    }

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%.*?at\s+(\S+).*?ETA\s+(\S+)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"(?:Destination|Merging formats into|has already been downloaded).*?[:\s]+(.+\.\w+)")]
    private static partial Regex DestinationRegex();

    [GeneratedRegex(@"^(https?:\/\/)?(www\.|m\.|web\.|fb\.)?(facebook\.com|fb\.watch|fb\.gg)\/(?:watch\/?\?v=\d+|reel\/\d+|[a-zA-Z0-9.\-_]+\/(?:videos|posts)\/\d+|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex FacebookUrlRegex();
}
