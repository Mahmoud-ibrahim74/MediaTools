namespace MediaTools.Domain.Entities;

public sealed class RasterImageFile
{
    public RasterImageFile(string filePath, string fileName, long fileSizeBytes, int width, int height, string formatHint)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        FileSizeBytes = fileSizeBytes;
        Width = width;
        Height = height;
        FormatHint = formatHint ?? throw new ArgumentNullException(nameof(formatHint));
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long FileSizeBytes { get; }
    public int Width { get; }
    public int Height { get; }
    public string FormatHint { get; }

    public string FormattedFileSize => FormatBytes(FileSizeBytes);

    public static RasterImageFile Create(string filePath, string fileName, long fileSizeBytes, int width, int height, string formatHint) =>
        new(filePath, fileName, fileSizeBytes, width, height, formatHint);

    private static string FormatBytes(long bytes)
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
