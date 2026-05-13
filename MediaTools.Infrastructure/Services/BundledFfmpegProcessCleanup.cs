using System.Diagnostics;
using System.IO;

namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Terminates FFmpeg/ffprobe processes that were started from this app's bundled tools directory
/// (<c>AppContext.BaseDirectory/ffmpeg</c>). Used on shutdown so orphaned encoder processes do not keep running.
/// </summary>
public static class BundledFfmpegProcessCleanup
{
    public static void KillRemainingBundledToolProcesses()
    {
        var toolsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        if (!Directory.Exists(toolsDir))
        {
            return;
        }

        var dirCmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var processName in new[] { "ffmpeg", "ffprobe" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var p in processes)
            {
                try
                {
                    string? exePath;
                    try
                    {
                        exePath = p.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied or 32/64-bit mismatch — skip.
                        continue;
                    }

                    if (string.IsNullOrEmpty(exePath))
                    {
                        continue;
                    }

                    var exeDir = Path.GetFullPath(Path.GetDirectoryName(exePath)!);
                    if (!exeDir.Equals(toolsDir, dirCmp))
                    {
                        continue;
                    }

                    if (p.HasExited)
                    {
                        continue;
                    }

                    try
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(milliseconds: 5000);
                    }
                    catch
                    {
                        // ignore
                    }
                }
                finally
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }
}
