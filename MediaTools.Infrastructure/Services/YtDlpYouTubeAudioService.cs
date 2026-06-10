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

    public Task EnsureToolsReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!ToolPaths.IsYtDlpReady)
        {
            throw new InvalidOperationException(
                "yt-dlp is not installed. Restart the application to trigger the download.");
        }

        return Task.CompletedTask;
    }

    public async Task<YouTubeVideoInfo> FetchVideoInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        await EnsureToolsReadyAsync(cancellationToken).ConfigureAwait(false);

        // Run the entire process on the thread pool so blocking reads never freeze the UI.
        return await Task.Run(async () =>
        {
            var args = $"--dump-single-json --flat-playlist --no-download \"{url}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ToolPaths.YtDlpExePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            SetFfmpegEnvVars(psi);

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
                throw new InvalidOperationException(
                    $"yt-dlp failed (exit {proc.ExitCode}): {stderr.Trim()}");
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

        var formatExt = MapFormatToExtension(request.AudioFormat);

        // yt-dlp arguments:
        // -x  = extract audio
        // --audio-format = target format
        // --audio-quality = bitrate for lossy
        // -o  = output template
        // --no-playlist/--yes-playlist = playlist handling
        // --newline = progress on each line
        // --ffmpeg-location = use our bundled ffmpeg
        var outputTemplate = Path.Combine(request.OutputFolderPath, "%(title)s.%(ext)s");
        var bitrateArg = IsLossyFormat(request.AudioFormat)
            ? $"--audio-quality {request.BitrateKbps}K"
            : "--audio-quality 0";

        var playlistArg = request.IsPlaylist ? "--yes-playlist" : "--no-playlist";

        var args = $"-x --audio-format {formatExt} {bitrateArg} " +
                   $"{playlistArg} --newline --no-mtime " +
                   $"--ffmpeg-location \"{ToolPaths.FfmpegDirectory}\" " +
                   $"-o \"{outputTemplate}\" \"{request.Url}\"";

        // Run the entire process on the thread pool.
        // Process.StandardOutput.EndOfStream is a BLOCKING property — running it on the
        // UI thread freezes the app. Task.Run ensures all blocking I/O stays off the UI.
        // Progress<T> marshals Report() calls back to the captured SynchronizationContext
        // automatically, so UI updates remain safe.
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

            SetFfmpegEnvVars(psi);

            using var proc = new Process { StartInfo = psi };
            var outputFilePath = string.Empty;
            int currentVideoIndex = 1;
            int totalVideos = 1;

            proc.Start();

            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            // Read stdout line-by-line. ReadLineAsync returns null at end-of-stream,
            // which avoids the blocking EndOfStream property.
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
                throw new InvalidOperationException(
                    $"yt-dlp download failed (exit {proc.ExitCode}): {stderr.Trim()}");
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

    private static void SetFfmpegEnvVars(ProcessStartInfo psi)
    {
        // Ensure yt-dlp finds our bundled FFmpeg
        var currentPath = psi.Environment.TryGetValue("PATH", out var p) ? p : "";
        psi.Environment["PATH"] = ToolPaths.FfmpegDirectory + ";" + currentPath;
    }

    /// <summary>Downloads yt-dlp.exe to the shared tools folder. Called during app startup.</summary>
    internal static async Task DownloadYtDlpAsync(CancellationToken cancellationToken)
    {
        const string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        Directory.CreateDirectory(ToolPaths.YtDlpDirectory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(ToolPaths.YtDlpExePath);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

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
