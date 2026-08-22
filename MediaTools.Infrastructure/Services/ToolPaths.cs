namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Central location for all external tool paths.
/// Dynamically uses local app data (<c>%LocalAppData%\MediaTools\Tools\</c>) or application base directory if writable.
/// </summary>
public static class ToolPaths
{
    /// <summary>Root directory for all downloaded tools.</summary>
    public static readonly string RootDirectory = ResolveRootDirectory();

    /// <summary>Directory containing ffmpeg.exe and ffprobe.exe.</summary>
    public static readonly string FfmpegDirectory =
        Path.Combine(RootDirectory, "ffmpeg");

    /// <summary>Full path to ffmpeg.exe.</summary>
    public static readonly string FfmpegExePath =
        Path.Combine(FfmpegDirectory, "ffmpeg.exe");

    /// <summary>Full path to ffprobe.exe.</summary>
    public static readonly string FfprobeExePath =
        Path.Combine(FfmpegDirectory, "ffprobe.exe");
    
    /// <summary>Directory containing yt-dlp.exe.</summary>
    public static readonly string YtDlpDirectory =
        Path.Combine(RootDirectory, "yt-dlp");

    /// <summary>Full path to yt-dlp.exe.</summary>
    public static readonly string YtDlpExePath =
        Path.Combine(YtDlpDirectory, "yt-dlp.exe");

    /// <summary>Returns true when both ffmpeg.exe and ffprobe.exe exist.</summary>
    public static bool IsFfmpegReady =>
        File.Exists(FfmpegExePath) && File.Exists(FfprobeExePath);

    /// <summary>Returns true when yt-dlp.exe exists.</summary>
    public static bool IsYtDlpReady =>
        File.Exists(YtDlpExePath);

    private static string ResolveRootDirectory()
    {
        // 1. Check if AppContext.BaseDirectory\Tools exists and is writable (dev/portable mode)
        var appBaseTools = Path.Combine(AppContext.BaseDirectory, "Tools");
        if (Directory.Exists(appBaseTools) && IsDirectoryWritable(appBaseTools))
        {
            return appBaseTools;
        }

        // 2. Standard Windows installed location: %LocalAppData%\MediaTools\Tools
        var localAppDataTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MediaTools",
            "Tools");

        return localAppDataTools;
    }

    private static bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "write_test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
