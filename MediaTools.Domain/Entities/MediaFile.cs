namespace MediaTools.Domain.Entities;

public sealed class MediaFile
{
    public MediaFile(string filePath, string fileName, long fileSizeBytes, TimeSpan duration, string format)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        FileSizeBytes = fileSizeBytes;
        Duration = duration;
        Format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long FileSizeBytes { get; }
    public TimeSpan Duration { get; }
    public string Format { get; }

    public string FormattedFileSize => FormatFileSize(FileSizeBytes);

    public static MediaFile Create(string filePath, string fileName, long fileSizeBytes, TimeSpan duration, string format) =>
        new(filePath, fileName, fileSizeBytes, duration, format);

    private static string FormatFileSize(long bytes)
    {
        const long kb = 1024;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var order = 0;
        while (size >= kb && order < units.Length - 1)
        {
            size /= kb;
            order++;
        }

        return $"{size:0.##} {units[order]}";
    }
}
