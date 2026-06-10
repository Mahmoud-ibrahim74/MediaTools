using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Domain.Enums;
using MediaTools.Presentation.Services;

namespace MediaTools.Presentation.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly ICompressionJobRepository _jobs;
    private readonly IUserPreferencesService _preferences;

    public DashboardViewModel(
        MainWindowViewModel shell,
        ICompressionJobRepository jobs,
        IUserPreferencesService preferences)
    {
        _shell = shell;
        _jobs = jobs;
        _preferences = preferences;
        _preferences.LifetimeStatsChanged += OnLifetimeStatsChanged;
    }

    private void OnLifetimeStatsChanged(object? sender, EventArgs e)
    {
        var app = global::System.Windows.Application.Current;
        if (app?.Dispatcher is null)
        {
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            RefreshStats();
        }
        else
        {
            app.Dispatcher.Invoke(RefreshStats);
        }
    }

    [ObservableProperty]
    private int _lifetimeVideoCompressedCount;

    [ObservableProperty]
    private int _lifetimePhotoEnhancedCount;

    [ObservableProperty]
    private int _lifetimeAudioEnhancedCount;

    [ObservableProperty]
    private int _lifetimeScreenRecordedCount;

    [ObservableProperty]
    private string _totalSpaceSavedDisplay = FormatBytes(0);

    [ObservableProperty]
    private string _averageCompressionRatioDisplay = "0%";

    /// <summary>Average space reduction percent (0–100) for charts.</summary>
    [ObservableProperty]
    private double _averageCompressionRatioPercent;

    [ObservableProperty]
    private double _aggregateStorageUsedPercent;

    [ObservableProperty]
    private string _aggregateStorageSummary = string.Empty;

    [ObservableProperty]
    private string _machineHeadline = Environment.MachineName;

    [ObservableProperty]
    private string _osSummary =
        $"{Environment.OSVersion.VersionString} · {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";

    public ObservableCollection<CompressionStatBarItem> CompressionBars { get; } = [];

    public ObservableCollection<DriveStorageDisplayItem> StorageDrives { get; } = [];

    /// <summary>Refresh compression stats, charts, and disk info (call when dashboard is shown).</summary>
    public void RefreshAll()
    {
        RefreshStats();
        RefreshStorage();
    }

    public void RefreshStats()
    {
        LifetimeVideoCompressedCount = _preferences.LifetimeVideoCompressedCount;
        LifetimePhotoEnhancedCount = _preferences.LifetimePhotoEnhancedCount;
        LifetimeAudioEnhancedCount = _preferences.LifetimeAudioEnhancedCount;
        LifetimeScreenRecordedCount = _preferences.LifetimeScreenRecordedCount;

        var completed = _jobs.GetAll().Where(j => j.Status == CompressionJobStatus.Completed).ToList();

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
        AverageCompressionRatioPercent = ratioCount == 0
            ? 0
            : (1 - ratioSum / ratioCount) * 100;
        AverageCompressionRatioDisplay = $"{AverageCompressionRatioPercent:0}%";

        RebuildCompressionBars(LifetimeVideoCompressedCount, saved, AverageCompressionRatioPercent);
    }

    private void RebuildCompressionBars(int lifetimeVideoSaveCount, long savedBytes, double avgReductionPercent)
    {
        CompressionBars.Clear();

        var videosBar = Math.Min(100, Math.Max(6, lifetimeVideoSaveCount * 12));
        var gbSaved = savedBytes / (1024d * 1024 * 1024);
        var spaceBar = Math.Min(100, gbSaved * 18 + 8);
        var ratioBar = Math.Clamp(avgReductionPercent, 4, 100);

        CompressionBars.Add(new CompressionStatBarItem
        {
            Label = "Video compressions (saved)",
            ValueText = lifetimeVideoSaveCount.ToString(),
            FillPercent = videosBar
        });

        CompressionBars.Add(new CompressionStatBarItem
        {
            Label = "Space reclaimed",
            ValueText = FormatBytes(savedBytes),
            FillPercent = spaceBar
        });

        CompressionBars.Add(new CompressionStatBarItem
        {
            Label = "Avg. size reduction",
            ValueText = $"{avgReductionPercent:0}%",
            FillPercent = ratioBar
        });
    }

    private void RefreshStorage()
    {
        StorageDrives.Clear();

        long sumTotal = 0;
        long sumUsed = 0;

        foreach (var drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            try
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                if (total <= 0)
                {
                    continue;
                }

                var used = total - free;
                sumTotal += total;
                sumUsed += used;

                var usedPct = 100d * used / total;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"Local Disk ({drive.Name.TrimEnd('\\')})"
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

                StorageDrives.Add(new DriveStorageDisplayItem
                {
                    Name = drive.Name,
                    TitleLine = label,
                    DetailLine =
                        $"{FormatBytes(free)} free · {FormatBytes(total)} total · {FormatBytes(used)} used",
                    UsedPercent = usedPct,
                    Severity = usedPct >= 92
                        ? DriveUsageSeverity.Critical
                        : usedPct >= 82
                            ? DriveUsageSeverity.Warning
                            : DriveUsageSeverity.Normal
                });
            }
            catch
            {
                // Ignore inaccessible volumes (permissions, removable quirks).
            }
        }

        if (sumTotal > 0)
        {
            AggregateStorageUsedPercent = 100d * sumUsed / sumTotal;
            AggregateStorageSummary =
                $"{StorageDrives.Count} fixed drive(s) · {FormatBytes(sumUsed)} used of {FormatBytes(sumTotal)}";
        }
        else
        {
            AggregateStorageUsedPercent = 0;
            AggregateStorageSummary = "No fixed drives detected.";
        }

        MachineHeadline = Environment.MachineName;
    }

    [RelayCommand]
    private void RefreshDashboard() => RefreshAll();

    [RelayCommand]
    private void GoToVideoCompress() => _shell.NavigateCommand.Execute("VideoCompress");

    [RelayCommand]
    private void GoToPhotoEnhancer() => _shell.NavigateCommand.Execute("PhotoEnhancer");

    [RelayCommand]
    private void GoToAudioEnhancer() => _shell.NavigateCommand.Execute("AudioEnhancer");

    [RelayCommand]
    private void GoToThumbnailGenerator() => _shell.NavigateCommand.Execute("ThumbnailGenerator");

    [RelayCommand]
    private void GoToScreenRecorder() => _shell.NavigateCommand.Execute("ScreenRecorder");

    [RelayCommand]
    private void GoToVideoEnhancer() => _shell.NavigateCommand.Execute("VideoEnhancer");

    [RelayCommand]
    private void GoToYouTubeAudio() => _shell.NavigateCommand.Execute("YouTubeAudio");

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
