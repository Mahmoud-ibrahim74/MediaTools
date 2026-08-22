using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Central helper for running and maintaining yt-dlp, detecting JavaScript runtimes
/// (Node.js, Deno, QuickJS, Bun), and formatting errors.
/// </summary>
public static class YtDlpProcessHelper
{
    private static readonly Lazy<(string? Flags, string? ExtraPath)> DetectedJsRuntime =
        new(DetectJsRuntimeInternal);

    /// <summary>
    /// Gets the recommended yt-dlp argument flags for JS runtime execution (e.g. <c>--js-runtimes node</c>).
    /// </summary>
    public static string? GetJsRuntimeArguments() => DetectedJsRuntime.Value.Flags;

    /// <summary>
    /// Configures <see cref="ProcessStartInfo"/> with environment PATH (FFmpeg + JS runtime)
    /// and UTF-8 encoding.
    /// </summary>
    public static void ConfigureProcessStartInfo(ProcessStartInfo psi)
    {
        var pathEntries = new List<string> { ToolPaths.FfmpegDirectory };

        if (!string.IsNullOrEmpty(DetectedJsRuntime.Value.ExtraPath))
        {
            pathEntries.Add(DetectedJsRuntime.Value.ExtraPath);
        }

        var currentPath = psi.Environment.TryGetValue("PATH", out var p) ? p : "";
        psi.Environment["PATH"] = string.Join(";", pathEntries) + ";" + currentPath;

        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
    }

    /// <summary>
    /// Appends resilience arguments (JS runtime flags, Windows safe filename flags) to yt-dlp arguments.
    /// </summary>
    public static string EnhanceArguments(string baseArgs)
    {
        var flags = new List<string>();

        var jsFlags = GetJsRuntimeArguments();
        if (!string.IsNullOrWhiteSpace(jsFlags))
        {
            flags.Add(jsFlags);
        }

        if (!baseArgs.Contains("--windows-filenames", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("--windows-filenames");
        }

        if (!baseArgs.Contains("--trim-filenames", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("--trim-filenames 100");
        }

        if (flags.Count == 0)
        {
            return baseArgs;
        }

        return $"{string.Join(" ", flags)} {baseArgs}";
    }

    /// <summary>
    /// Ensures yt-dlp is downloaded and up to date.
    /// If missing, downloads it immediately. If older than 7 days, triggers an update.
    /// </summary>
    public static async Task EnsureYtDlpReadyAndUpdatedAsync(CancellationToken cancellationToken = default)
    {
        if (!ToolPaths.IsYtDlpReady)
        {
            await DownloadYtDlpAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var fileInfo = new FileInfo(ToolPaths.YtDlpExePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(7))
            {
                // Trigger an update
                await TrySelfUpdateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Non-fatal if update check fails during startup
        }
    }

    /// <summary>
    /// Downloads the latest yt-dlp.exe directly from the official GitHub release.
    /// </summary>
    public static async Task DownloadYtDlpAsync(CancellationToken cancellationToken = default)
    {
        const string downloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        Directory.CreateDirectory(ToolPaths.YtDlpDirectory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tempPath = Path.Combine(ToolPaths.YtDlpDirectory, $"yt-dlp_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = File.Create(tempPath))
            {
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, ToolPaths.YtDlpExePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Attempts to update yt-dlp using its built-in self-update mechanism (<c>yt-dlp -U</c>)
    /// or falls back to re-downloading.
    /// </summary>
    public static async Task<bool> TrySelfUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!ToolPaths.IsYtDlpReady)
        {
            await DownloadYtDlpAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolPaths.YtDlpExePath,
                Arguments = "-U",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            ConfigureProcessStartInfo(psi);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(45));

            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            // If process update fails, attempt direct download
            try
            {
                await DownloadYtDlpAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Formats and cleans stderr from yt-dlp to extract the real error message for user display.
    /// </summary>
    public static string FormatErrorMessage(int exitCode, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return $"yt-dlp process exited with error code {exitCode}.";
        }

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Find actual ERROR: lines
        var errorLines = lines
            .Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["ERROR:".Length..].Trim())
            .ToList();

        if (errorLines.Count > 0)
        {
            var mainError = string.Join("; ", errorLines);

            if (mainError.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase) ||
                mainError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            {
                return "YouTube blocked the download stream (HTTP 403 Forbidden). Updating yt-dlp or trying again in a few moments may resolve this.";
            }

            return mainError;
        }

        // Filter out benign warnings
        var significantLines = lines
            .Where(l => !l.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("See https://github.com/yt-dlp/yt-dlp/wiki/EJS", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (significantLines.Count > 0)
        {
            return significantLines[^1];
        }

        return lines[^1];
    }

    private static (string? Flags, string? ExtraPath) DetectJsRuntimeInternal()
    {
        // Check for Node.js in standard locations
        var candidatePaths = new (string RuntimeName, string ExePath)[]
        {
            ("node", @"C:\Program Files\nodejs\node.exe"),
            ("node", @"C:\Program Files (x86)\nodejs\node.exe"),
            ("node", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\nodejs\node.exe")),
            ("node", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\node.exe")),
            ("deno", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".deno\bin\deno.exe")),
            ("bun", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".bun\bin\bun.exe")),
            ("quickjs", Path.Combine(ToolPaths.RootDirectory, @"quickjs\qjs.exe")),
            ("deno", Path.Combine(ToolPaths.RootDirectory, @"deno\deno.exe")),
            ("node", Path.Combine(ToolPaths.RootDirectory, @"nodejs\node.exe"))
        };

        foreach (var (runtime, path) in candidatePaths)
        {
            if (File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                return ($"--js-runtimes {runtime}", dir);
            }
        }

        // Check if node/deno/bun/qjs is available in PATH
        if (IsExecutableInPath("node.exe"))
        {
            return ("--js-runtimes node", null);
        }

        if (IsExecutableInPath("deno.exe"))
        {
            return ("--js-runtimes deno", null);
        }

        if (IsExecutableInPath("bun.exe"))
        {
            return ("--js-runtimes bun", null);
        }

        if (IsExecutableInPath("qjs.exe") || IsExecutableInPath("quickjs.exe"))
        {
            return ("--js-runtimes quickjs", null);
        }

        return (null, null);
    }

    private static bool IsExecutableInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return false;
        }

        var paths = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, exeName);
                if (File.Exists(fullPath))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore path errors
            }
        }

        return false;
    }
}
