using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly ICompressionJobRepository _jobs;

    public DashboardViewModel(MainWindowViewModel shell, ICompressionJobRepository jobs)
    {
        _shell = shell;
        _jobs = jobs;
        RefreshStats();
    }

    [ObservableProperty]
    private int _totalVideosCompressed;

    [ObservableProperty]
    private string _totalSpaceSavedDisplay = FormatBytes(0);

    [ObservableProperty]
    private string _averageCompressionRatioDisplay = "0%";

    public void RefreshStats()
    {
        var completed = _jobs.GetAll().Where(j => j.Status == CompressionJobStatus.Completed).ToList();
        TotalVideosCompressed = completed.Count;

        long saved = 0;
        double ratioSum = 0;
        var ratioCount = 0;
        foreach (var job in completed)
        {
            if (job.OutputSizeBytes is { } outSize && job.SourceFile.FileSizeBytes > outSize)
            {
                saved += job.SourceFile.FileSizeBytes - outSize;
                ratioSum += (double)outSize / job.SourceFile.FileSizeBytes;
                ratioCount++;
            }
        }

        TotalSpaceSavedDisplay = FormatBytes(saved);
        AverageCompressionRatioDisplay = ratioCount == 0
            ? "0%"
            : $"{(1 - ratioSum / ratioCount) * 100:0}%";
    }

    [RelayCommand]
    private void GoToVideoCompress() => _shell.NavigateCommand.Execute("VideoCompress");

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
