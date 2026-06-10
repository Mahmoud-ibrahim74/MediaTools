namespace MediaTools.Infrastructure.Services;

/// <summary>
/// Central location for all external tool paths.
/// Tools are stored under <c>%TEMP%\MediaTools\</c> so the application directory stays clean
/// and tools survive across app updates in the same location.
/// </summary>
public static class ToolPaths
{
    /// <summary>Root directory for all downloaded tools.</summary>
    public static readonly string RootDirectory =
        Path.Combine(Path.GetTempPath(), "MediaTools");

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
}
