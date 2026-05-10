using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain;
using MediaTools.Domain.Enums;
using MediaTools.Presentation.Helpers;
using MediaTools.Presentation.Services;
using MediaTools.Presentation.Undo;

namespace MediaTools.Presentation.ViewModels;

public partial class SubtitleExtractorViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".webm"
    ];

    private readonly ProcessSubtitleExtractUseCase _processSubtitleExtractUseCase;
    private readonly ISubtitleExtractorService _subtitleExtractorService;
    private readonly IUserPreferencesService _preferences;
    private readonly IWindowsToastNotificationService _toastNotifications;
    private readonly UndoRedoHost<SubtitleExtractorUndoSnapshot> _history;
    private CancellationTokenSource? _cts;
    private bool _suppressUndoNotification;

    public SubtitleExtractorViewModel(
        ProcessSubtitleExtractUseCase processSubtitleExtractUseCase,
        ISubtitleExtractorService subtitleExtractorService,
        IUserPreferencesService preferences,
        IWindowsToastNotificationService toastNotifications)
    {
        _processSubtitleExtractUseCase = processSubtitleExtractUseCase;
        _subtitleExtractorService = subtitleExtractorService;
        _preferences = preferences;
        _toastNotifications = toastNotifications;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;

        _history = new UndoRedoHost<SubtitleExtractorUndoSnapshot>(
            CaptureSnapshot,
            ApplySnapshot,
            CaptureSnapshot(),
            OnUndoRedoHistoryChanged);

        SubtitleTracks.CollectionChanged += OnSubtitleTracksCollectionChanged;
    }

    private void OnSubtitleTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        ExtractSubtitleCommand.NotifyCanExecuteChanged();

    public IEnumerable<SubtitleExportFormat> ExportFormats => Enum.GetValues<SubtitleExportFormat>();

    public ObservableCollection<SubtitleTrackInfoDto> SubtitleTracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileInfoCard))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _durationDisplay = string.Empty;

    [ObservableProperty]
    private string _formatDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private SubtitleTrackInfoDto? _selectedTrack;

    [ObservableProperty]
    private SubtitleExportFormat _exportFormat = SubtitleExportFormat.SubRip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private double _progressPercent01;

    [ObservableProperty]
    private string _progressStatusText = string.Empty;

    [ObservableProperty]
    private string _progressDetailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    [NotifyPropertyChangedFor(nameof(ShowCancelButton))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressCard))]
    private bool _finishedAttempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultCard))]
    private bool _succeeded;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private bool _isDropHover;

    public bool ShowFileInfoCard => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowSubtitleTrackPicker => ShowFileInfoCard && SubtitleTracks.Count > 0;

    public bool ShowNoSubtitleTracksWarning => ShowFileInfoCard && !IsAnalyzing && SubtitleTracks.Count == 0;

    public bool ShowProgressCard => IsRunning || FinishedAttempt;

    public bool ShowResultCard => Succeeded;

    public bool ShowCancelButton => IsRunning;

    public int ProgressPercentDisplay => (int)Math.Round(ProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private bool CanStartExtract() =>
        !string.IsNullOrWhiteSpace(SelectedFilePath)
        && SelectedTrack is not null
        && Directory.Exists(_preferences.SaveFolderPath)
        && !IsRunning
        && !IsAnalyzing;

    private bool CanUndoOperation() => _history.CanUndo && !IsRunning;

    private bool CanRedoOperation() => _history.CanRedo && !IsRunning;

    private void OnUndoRedoHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void NotifyUndoableEdit()
    {
        if (_suppressUndoNotification || _history.IsApplyingHistory)
        {
            return;
        }

        _history.NotifyEdit();
    }

    public void HandleDrop(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                continue;
            }

            if (AllowedExtensions.Contains(ext.ToLowerInvariant()))
            {
                _ = LoadFromDropWithUndoAsync(path);
                break;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.m4v;*.webm|All files|*.*"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadFileAsync(dlg.FileName).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (loaded)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
    }

    [RelayCommand]
    private void ClearFile()
    {
        _history.PushUndoFrameAnd(() =>
        {
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DurationDisplay = string.Empty;
            FormatDisplay = string.Empty;
            SubtitleTracks.Clear();
            SelectedTrack = null;
            IsAnalyzing = false;
            FinishedAttempt = false;
            Succeeded = false;
            ProgressPercent01 = 0;
            ProgressStatusText = string.Empty;
            ProgressDetailText = string.Empty;
            ResultMessage = string.Empty;
        });
    }

    [RelayCommand(CanExecute = nameof(CanUndoOperation))]
    private void Undo() => _history.TryUndo();

    [RelayCommand(CanExecute = nameof(CanRedoOperation))]
    private void Redo() => _history.TryRedo();

    [RelayCommand(CanExecute = nameof(CanStartExtract))]
    private async Task ExtractSubtitleAsync()
    {
        if (SelectedFilePath is null || SelectedTrack is null || !CanStartExtract())
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsRunning = true;
        FinishedAttempt = false;
        Succeeded = false;
        ProgressPercent01 = 0;
        ProgressStatusText = "Extracting…";
        ProgressDetailText = string.Empty;
        ResultMessage = string.Empty;

        var ext = ExportFormat == SubtitleExportFormat.Copy
            ? SubtitleCodecFileExtensions.SuggestExtension(SelectedTrack.Codec)
            : ExtensionFor(ExportFormat);

        var outputPath = Path.Combine(
            _preferences.SaveFolderPath,
            $"{Path.GetFileNameWithoutExtension(SelectedFilePath)}_sub_{SelectedTrack.StreamIndex}{ext}");

        var request = new ProcessSubtitleExtractRequest(
            SelectedFilePath,
            outputPath,
            SelectedTrack.StreamIndex,
            ExportFormat);

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<SubtitleExtractProgressReport>(r =>
        {
            void Apply()
            {
                ProgressPercent01 = r.Percent01;
                ProgressDetailText = r.StepDescription;
            }

            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                dispatcher.Invoke(Apply);
            }
        });

        string? toastTitle = null;
        string? toastBody = null;
        var toastSuccess = false;

        try
        {
            var result = await _processSubtitleExtractUseCase.ExecuteAsync(request, progress, token).ConfigureAwait(true);

            if (result.IsCancelled)
            {
                ProgressStatusText = "Cancelled";
                Succeeded = false;
            }
            else if (result.IsSuccess && File.Exists(outputPath))
            {
                Succeeded = true;
                ProgressStatusText = "Complete";
                ProgressPercent01 = 1;
                var len = new FileInfo(outputPath).Length;
                ResultMessage = $"Saved to {outputPath} ({FormatBytes(len)})";
                toastTitle = "Subtitle extracted";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = "Output file was not created.";
                ResultMessage = ProgressDetailText;
                toastTitle = "Subtitle extract failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
            else
            {
                Succeeded = false;
                ProgressStatusText = "Failed";
                ProgressDetailText = result.ErrorMessage ?? "Unknown error";
                ResultMessage = result.ErrorMessage ?? "Extraction failed.";
                toastTitle = "Subtitle extract failed";
                toastBody = ResultMessage;
                toastSuccess = false;
            }
        }
        finally
        {
            IsRunning = false;
            FinishedAttempt = true;
            ExtractSubtitleCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _history.FlushPendingEdit();

            if (toastTitle is not null)
            {
                _toastNotifications.ShowToolFinished(
                    toastTitle,
                    toastBody ?? string.Empty,
                    toastSuccess,
                    "Subtitle Extractor");
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var folder = _preferences.SaveFolderPath;
        if (!Directory.Exists(folder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private async Task LoadFromDropWithUndoAsync(string path)
    {
        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadFileAsync(path).ConfigureAwait(true);
        }
        finally
        {
            _suppressUndoNotification = false;
        }

        if (loaded)
        {
            _history.EndUndoGroup();
        }
        else
        {
            _history.CancelUndoGroup();
        }
    }

    private static string ExtensionFor(SubtitleExportFormat format) =>
        format switch
        {
            SubtitleExportFormat.SubRip => ".srt",
            SubtitleExportFormat.WebVtt => ".vtt",
            SubtitleExportFormat.Ass => ".ass",
            SubtitleExportFormat.Copy => ".sub",
            _ => ".srt"
        };

    private async Task<bool> LoadFileAsync(string path)
    {
        IsAnalyzing = true;
        SubtitleTracks.Clear();
        SelectedTrack = null;
        try
        {
            var analysis = await _subtitleExtractorService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = analysis.FileName;
            FileSizeDisplay = FormatBytes(analysis.FileSizeBytes);
            DurationDisplay = analysis.Duration is { } d ? FormatDuration(d) : "—";
            FormatDisplay = analysis.ContainerFormatHint;

            foreach (var t in analysis.SubtitleTracks)
            {
                SubtitleTracks.Add(t);
            }

            SelectedTrack = SubtitleTracks.FirstOrDefault();
            FinishedAttempt = false;
            Succeeded = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not read media: {ex.Message}");
            SelectedFilePath = null;
            FileDisplayName = string.Empty;
            FileSizeDisplay = string.Empty;
            DurationDisplay = string.Empty;
            FormatDisplay = string.Empty;
            return false;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private SubtitleExtractorUndoSnapshot CaptureSnapshot() =>
        new(
            SelectedFilePath,
            FileDisplayName,
            FileSizeDisplay,
            DurationDisplay,
            FormatDisplay,
            SubtitleTracks.ToArray(),
            SelectedTrack?.StreamIndex,
            ExportFormat,
            ProgressPercent01,
            ProgressStatusText,
            ProgressDetailText,
            FinishedAttempt,
            Succeeded,
            ResultMessage);

    private void ApplySnapshot(SubtitleExtractorUndoSnapshot s)
    {
        SelectedFilePath = s.SelectedFilePath;
        FileDisplayName = s.FileDisplayName;
        FileSizeDisplay = s.FileSizeDisplay;
        DurationDisplay = s.DurationDisplay;
        FormatDisplay = s.FormatDisplay;
        SubtitleTracks.Clear();
        foreach (var t in s.SubtitleTracks)
        {
            SubtitleTracks.Add(t);
        }

        SelectedTrack = s.SelectedStreamIndex is { } idx
            ? SubtitleTracks.FirstOrDefault(t => t.StreamIndex == idx)
            : null;

        ExportFormat = s.ExportFormat;
        ProgressPercent01 = s.ProgressPercent01;
        ProgressStatusText = s.ProgressStatusText;
        ProgressDetailText = s.ProgressDetailText;
        FinishedAttempt = s.FinishedAttempt;
        Succeeded = s.Succeeded;
        ResultMessage = s.ResultMessage;
        IsAnalyzing = false;
    }

    partial void OnExportFormatChanged(SubtitleExportFormat value) => NotifyUndoableEdit();

    partial void OnSelectedTrackChanged(SubtitleTrackInfoDto? value)
    {
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
        NotifyUndoableEdit();
    }

    partial void OnSelectedFilePathChanged(string? value) =>
        ExtractSubtitleCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }

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

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
}
