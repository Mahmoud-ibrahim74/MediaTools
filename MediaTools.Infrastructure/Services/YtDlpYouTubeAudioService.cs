using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Downloads audio from YouTube using yt-dlp (auto-downloaded on first use) and
/// leverages the app's bundled FFmpeg for audio conversion.
/// </summary>
public sealed partial class YtDlpYouTubeAudioService : IYouTubeAudioService
{
    public async Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default)
    {
        await YtDlpProcessHelper.EnsureYtDlpReadyAndUpdatedAsync(cancellationToken).ConfigureAwait(false);

        if (!ToolPaths.IsYtDlpReady)
        {
            throw new InvalidOperationException(
                "yt-dlp is not installed. Restart the application to trigger the download.");
        }
    }

    public async Task<YouTubeVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        return await FetchVideoInfoInternalAsync(url, allowRetryOn403: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<YouTubeVideoInfo> FetchVideoInfoInternalAsync(
        string url,
        bool allowRetryOn403,
        CancellationToken cancellationToken)
    {
        // Run the entire process on the thread pool so blocking reads never freeze the UI.
        return await Task.Run(async () =>
        {
            var rawArgs = $"--dump-single-json --flat-playlist --no-download \"{url}\"";
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
                if (allowRetryOn403 && (stderr.Contains("403") || stderr.Contains("Forbidden") || stderr.Contains("extractor")))
                {
                    var updated = await YtDlpProcessHelper.TrySelfUpdateAsync(cancellationToken).ConfigureAwait(false);
                    if (updated)
                    {
                        return await FetchVideoInfoInternalAsync(url, allowRetryOn403: false, cancellationToken).ConfigureAwait(false);
                    }
                }

                throw new InvalidOperationException(
                    $"yt-dlp failed (exit {proc.ExitCode}): {YtDlpProcessHelper.FormatErrorMessage(proc.ExitCode, stderr)}");
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            bool isPlaylist = false;
            if (root.TryGetProperty("_type", out var typeProp) && typeProp.GetString() == "playlist")
            {
                isPlaylist = true;
            }

            var title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "Unknown" : "Unknown";
            var channel = root.TryGetProperty("uploader", out var upProp) ? upProp.GetString() ?? "" :
                          root.TryGetProperty("playlist_uploader", out var plUpProp) ? plUpProp.GetString() ?? "" :
                          root.TryGetProperty("channel", out var chProp) ? chProp.GetString() ?? "" : "";

            string? thumbnail = null;
            if (root.TryGetProperty("thumbnail", out var thProp))
            {
                thumbnail = thProp.GetString();
            }
            if (string.IsNullOrEmpty(thumbnail) && root.TryGetProperty("thumbnails", out var thsProp) && thsProp.ValueKind == JsonValueKind.Array && thsProp.GetArrayLength() > 0)
            {
                var lastTh = thsProp[thsProp.GetArrayLength() - 1];
                if (lastTh.TryGetProperty("url", out var urlProp))
                {
                    thumbnail = urlProp.GetString();
                }
            }

            TimeSpan duration = TimeSpan.Zero;
            long viewCount = 0;
            int videoCount = 0;
            List<PlaylistItemInfo>? playlistItems = null;

            if (isPlaylist)
            {
                if (root.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
                {
                    playlistItems = new List<PlaylistItemInfo>();
                    foreach (var entry in entriesProp.EnumerateArray())
                    {
                        var itemTitle = entry.TryGetProperty("title", out var itProp) ? itProp.GetString() ?? "Unknown" : "Unknown";
                        
                        var itemUrl = entry.TryGetProperty("url", out var iuProp) ? iuProp.GetString() : null;
                        if (string.IsNullOrEmpty(itemUrl) && entry.TryGetProperty("id", out var idProp))
                        {
                            itemUrl = $"https://www.youtube.com/watch?v={idProp.GetString()}";
                        }
                        
                        var itemChannel = entry.TryGetProperty("uploader", out var iuprProp) ? iuprProp.GetString() : null;
                        var itemDurationSec = entry.TryGetProperty("duration", out var idurProp) && idurProp.ValueKind == JsonValueKind.Number ? idurProp.GetDouble() : 0;

                        if (!string.IsNullOrEmpty(itemUrl))
                        {
                            playlistItems.Add(new PlaylistItemInfo(itemTitle, itemUrl, itemChannel, TimeSpan.FromSeconds(itemDurationSec)));
                        }
                    }
                    videoCount = playlistItems.Count;
                }
                
                if (videoCount == 0 && root.TryGetProperty("playlist_count", out var plcProp))
                {
                    videoCount = plcProp.GetInt32();
                }
            }
            else
            {
                var durationSec = root.TryGetProperty("duration", out var dur) ? dur.GetDouble() : 0;
                duration = TimeSpan.FromSeconds(durationSec);
                viewCount = root.TryGetProperty("view_count", out var vc) ? vc.GetInt64() : 0;
            }

            return new YouTubeVideoInfo(
                Title: title,
                ChannelName: channel,
                Duration: duration,
                ThumbnailUrl: thumbnail,
                ViewCount: viewCount,
                IsPlaylist: isPlaylist,
                VideoCount: videoCount,
                PlaylistItems: playlistItems);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadAudioAsync(
        YouTubeDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(request.OutputFolderPath);

        return await DownloadAudioInternalAsync(request, progress, allowRetryOn403: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> DownloadAudioInternalAsync(
        YouTubeDownloadRequest request,
        IProgress<YouTubeDownloadProgress> progress,
        bool allowRetryOn403,
        CancellationToken cancellationToken)
    {
        var formatExt = MapFormatToExtension(request.AudioFormat);

        var outputTemplate = Path.Combine(request.OutputFolderPath, "%(title)s.%(ext)s");
        var bitrateArg = IsLossyFormat(request.AudioFormat)
            ? $"--audio-quality {request.BitrateKbps}K"
            : "--audio-quality 0";

        var playlistArg = request.IsPlaylist ? "--yes-playlist" : "--no-playlist";

        var rawArgs = $"-x --audio-format {formatExt} {bitrateArg} " +
                      $"{playlistArg} --newline --no-mtime " +
                      $"--ffmpeg-location \"{ToolPaths.FfmpegDirectory}\" " +
                      $"-o \"{outputTemplate}\" \"{request.Url}\"";

        var args = YtDlpProcessHelper.EnhanceArguments(rawArgs);

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
            int currentVideoIndex = 1;
            int totalVideos = 1;

            proc.Start();

            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(cancellationToken)
                       .ConfigureAwait(false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Detect playlist item indexing: [download] Downloading item 1 of 5
                var playlistMatch = PlaylistIndexRegex().Match(line);
                if (playlistMatch.Success)
                {
                    if (int.TryParse(playlistMatch.Groups[1].Value, out var idx))
                    {
                        currentVideoIndex = idx;
                    }
                    if (int.TryParse(playlistMatch.Groups[2].Value, out var tot))
                    {
                        totalVideos = tot;
                    }
                }

                // Detect output file path from yt-dlp output
                var destMatch = DestinationRegex().Match(line);
                if (destMatch.Success)
                {
                    outputFilePath = destMatch.Groups[1].Value.Trim();
                }

                // Parse download progress: [download]  42.5% of ~5.30MiB at  2.50MiB/s ETA 00:03
                var progressMatch = ProgressRegex().Match(line);
                if (progressMatch.Success)
                {
                    var pct = double.TryParse(progressMatch.Groups[1].Value, out var p) ? p : 0;
                    var speed = progressMatch.Groups.Count > 2 ? progressMatch.Groups[2].Value : null;
                    var eta = progressMatch.Groups.Count > 3 ? progressMatch.Groups[3].Value : null;

                    double overallPct;
                    string statusText;
                    if (request.IsPlaylist)
                    {
                        overallPct = ((currentVideoIndex - 1) * 100.0 + pct) / totalVideos;
                        overallPct = Math.Clamp(overallPct, 0, 100);
                        statusText = $"Downloading video {currentVideoIndex} of {totalVideos} ({pct:0.0}%)";
                    }
                    else
                    {
                        overallPct = pct;
                        statusText = $"Downloading… {pct:0.0}%";
                    }

                    progress.Report(new YouTubeDownloadProgress(
                        ProgressPercent: overallPct,
                        StatusText: statusText,
                        DownloadSpeedDisplay: speed,
                        EtaDisplay: eta));
                }

                // Detect post-processing phase
                if (line.Contains("[ExtractAudio]") || line.Contains("[ffmpeg]"))
                {
                    double overallPct = 95;
                    string statusText = "Converting audio…";
                    if (request.IsPlaylist)
                    {
                        overallPct = ((currentVideoIndex - 1) * 100.0 + 95) / totalVideos;
                        overallPct = Math.Clamp(overallPct, 0, 100);
                        statusText = $"Converting video {currentVideoIndex} of {totalVideos}…";
                    }

                    progress.Report(new YouTubeDownloadProgress(
                        ProgressPercent: overallPct,
                        StatusText: statusText,
                        DownloadSpeedDisplay: null,
                        EtaDisplay: null));
                }
            }

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                if (allowRetryOn403 && (stderr.Contains("403") || stderr.Contains("Forbidden") || stderr.Contains("extractor")))
                {
                    var updated = await YtDlpProcessHelper.TrySelfUpdateAsync(cancellationToken).ConfigureAwait(false);
                    if (updated)
                    {
                        return await DownloadAudioInternalAsync(request, progress, allowRetryOn403: false, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                throw new InvalidOperationException(
                    $"yt-dlp download failed (exit {proc.ExitCode}): {YtDlpProcessHelper.FormatErrorMessage(proc.ExitCode, stderr)}");
            }

            if (request.IsPlaylist)
            {
                progress.Report(new YouTubeDownloadProgress(
                    ProgressPercent: 100,
                    StatusText: "Download complete",
                    DownloadSpeedDisplay: null,
                    EtaDisplay: null));

                return request.OutputFolderPath;
            }

            // If we didn't catch the destination from stdout, try to find the file
            if (string.IsNullOrWhiteSpace(outputFilePath) || !File.Exists(outputFilePath))
            {
                outputFilePath = FindLatestFileInDirectory(request.OutputFolderPath, $"*.{formatExt}");
            }

            progress.Report(new YouTubeDownloadProgress(
                ProgressPercent: 100,
                StatusText: "Download complete",
                DownloadSpeedDisplay: null,
                EtaDisplay: null));

            return outputFilePath ?? throw new InvalidOperationException("Output file was not created.");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads yt-dlp.exe to the shared tools folder. Called during app startup.</summary>
    internal static Task DownloadYtDlpAsync(CancellationToken cancellationToken) =>
        YtDlpProcessHelper.DownloadYtDlpAsync(cancellationToken);

    private static string MapFormatToExtension(YouTubeAudioFormat format) =>
        format switch
        {
            YouTubeAudioFormat.Mp3 => "mp3",
            YouTubeAudioFormat.Aac => "m4a",
            YouTubeAudioFormat.Flac => "flac",
            YouTubeAudioFormat.Wav => "wav",
            YouTubeAudioFormat.Ogg => "vorbis",
            YouTubeAudioFormat.Opus => "opus",
            _ => "mp3"
        };

    private static bool IsLossyFormat(YouTubeAudioFormat format) =>
        format is YouTubeAudioFormat.Mp3 or YouTubeAudioFormat.Aac or YouTubeAudioFormat.Ogg or YouTubeAudioFormat.Opus;

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

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%.*?at\s+(\S+).*?ETA\s+(\S+)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"(?:Destination|Merging formats into|has already been downloaded).*?[:\s]+(.+\.\w+)")]
    private static partial Regex DestinationRegex();

    [GeneratedRegex(@"\[download\]\s+Downloading\s+(?:item|video)\s+(\d+)\s+of\s+(\d+)")]
    private static partial Regex PlaylistIndexRegex();
}
