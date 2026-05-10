using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain;
using MediaTools.Domain.Enums;
using MediaTools.Presentation.Helpers;
using MediaTools.Presentation.Undo;

namespace MediaTools.Presentation.ViewModels;

public partial class VideoEnhancerViewModel
{
    private readonly ProcessSubtitleExtractUseCase _processSubtitleExtractUseCase;
    private readonly ISubtitleExtractorService _subtitleExtractorService;
    private CancellationTokenSource? _subtitleCts;

    public IEnumerable<SubtitleExportFormat> SubtitleExportFormats => Enum.GetValues<SubtitleExportFormat>();

    public ObservableCollection<SubtitleTrackInfoDto> SubtitleTracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubFileInfoCard))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private string? _subSelectedFilePath;

    [ObservableProperty]
    private string _subFileDisplayName = string.Empty;

    [ObservableProperty]
    private string _subFileSizeDisplay = string.Empty;

    [ObservableProperty]
    private string _subDurationDisplay = string.Empty;

    [ObservableProperty]
    private string _subFormatDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private bool _subIsAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSubtitleTracksWarning))]
    private SubtitleTrackInfoDto? _subSelectedTrack;

    [ObservableProperty]
    private SubtitleExportFormat _subExportFormat = SubtitleExportFormat.SubRip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubProgressPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(ShowSubProgressCard))]
    private double _subProgressPercent01;

    [ObservableProperty]
    private string _subProgressStatusText = string.Empty;

    [ObservableProperty]
    private string _subProgressDetailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubProgressCard))]
    [NotifyPropertyChangedFor(nameof(ShowSubCancelButton))]
    private bool _subIsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubProgressCard))]
    private bool _subFinishedAttempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubResultCard))]
    private bool _subSucceeded;

    [ObservableProperty]
    private string _subResultMessage = string.Empty;

    [ObservableProperty]
    private bool _subIsDropHover;

    public bool ShowSubFileInfoCard => !string.IsNullOrWhiteSpace(SubSelectedFilePath);

    public bool ShowSubtitleTrackPicker => ShowSubFileInfoCard && SubtitleTracks.Count > 0;

    public bool ShowNoSubtitleTracksWarning => ShowSubFileInfoCard && !SubIsAnalyzing && SubtitleTracks.Count == 0;

    public bool ShowSubProgressCard => SubIsRunning || SubFinishedAttempt;

    public bool ShowSubResultCard => SubSucceeded;

    public bool ShowSubCancelButton => SubIsRunning;

    public int SubProgressPercentDisplay => (int)Math.Round(SubProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private void OnSubtitleTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartSubtitleExtract() =>
        !string.IsNullOrWhiteSpace(SubSelectedFilePath)
        && SubSelectedTrack is not null
        && Directory.Exists(_preferences.SaveFolderPath)
        && !SubIsRunning
        && !SubIsAnalyzing;

    private SubtitleExtractorUndoSnapshot CaptureSubtitleSnapshot() =>
        new(
            SubSelectedFilePath,
            SubFileDisplayName,
            SubFileSizeDisplay,
            SubDurationDisplay,
            SubFormatDisplay,
            SubtitleTracks.ToArray(),
            SubSelectedTrack?.StreamIndex,
            SubExportFormat,
            SubProgressPercent01,
            SubProgressStatusText,
            SubProgressDetailText,
            SubFinishedAttempt,
            SubSucceeded,
            SubResultMessage);

    private void ApplySubtitleUndoState(SubtitleExtractorUndoSnapshot s)
    {
        SubSelectedFilePath = s.SelectedFilePath;
        SubFileDisplayName = s.FileDisplayName;
        SubFileSizeDisplay = s.FileSizeDisplay;
        SubDurationDisplay = s.DurationDisplay;
        SubFormatDisplay = s.FormatDisplay;
        SubtitleTracks.Clear();
        foreach (var t in s.SubtitleTracks)
        {
            SubtitleTracks.Add(t);
        }

        SubSelectedTrack = s.SelectedStreamIndex is { } idx
            ? SubtitleTracks.FirstOrDefault(t => t.StreamIndex == idx)
            : null;

        SubExportFormat = s.ExportFormat;
        SubProgressPercent01 = s.ProgressPercent01;
        SubProgressStatusText = s.ProgressStatusText;
        SubProgressDetailText = s.ProgressDetailText;
        SubFinishedAttempt = s.FinishedAttempt;
        SubSucceeded = s.Succeeded;
        SubResultMessage = s.ResultMessage;
        SubIsAnalyzing = false;

        OnPropertyChanged(nameof(ShowSubFileInfoCard));
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }

    public void HandleSubtitleDrop(IEnumerable<string> paths)
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
                _ = LoadSubtitleFromDropWithUndoAsync(path);
                break;
            }
        }
    }

    private async Task LoadSubtitleFromDropWithUndoAsync(string path)
    {
        _history.BeginUndoGroup();
        _suppressUndoNotification = true;
        bool loaded;
        try
        {
            loaded = await LoadSubtitleFileAsync(path).ConfigureAwait(true);
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
    private async Task BrowseSubtitleFileAsync()
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
            loaded = await LoadSubtitleFileAsync(dlg.FileName).ConfigureAwait(true);
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
    private void ClearSubtitleFile()
    {
        _history.PushUndoFrameAnd(ResetSubUiToNoFile);
    }

    private void ResetSubUiToNoFile()
    {
        SubSelectedFilePath = null;
        SubFileDisplayName = string.Empty;
        SubFileSizeDisplay = string.Empty;
        SubDurationDisplay = string.Empty;
        SubFormatDisplay = string.Empty;
        SubtitleTracks.Clear();
        SubSelectedTrack = null;
        SubIsAnalyzing = false;
        SubFinishedAttempt = false;
        SubSucceeded = false;
        SubProgressPercent01 = 0;
        SubProgressStatusText = string.Empty;
        SubProgressDetailText = string.Empty;
        SubResultMessage = string.Empty;

        OnPropertyChanged(nameof(ShowSubFileInfoCard));
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
    }

    [RelayCommand(CanExecute = nameof(CanStartSubtitleExtract))]
    private async Task ExtractSubtitleAsync()
    {
        if (SubSelectedFilePath is null || SubSelectedTrack is null || !CanStartSubtitleExtract())
        {
            return;
        }

        _subtitleCts = new CancellationTokenSource();
        var token = _subtitleCts.Token;

        SubIsRunning = true;
        SubFinishedAttempt = false;
        SubSucceeded = false;
        SubProgressPercent01 = 0;
        SubProgressStatusText = "Extracting…";
        SubProgressDetailText = string.Empty;
        SubResultMessage = string.Empty;

        var ext = SubExportFormat == SubtitleExportFormat.Copy
            ? SubtitleCodecFileExtensions.SuggestExtension(SubSelectedTrack.Codec)
            : SubtitleExtensionFor(SubExportFormat);

        var outputPath = Path.Combine(
            _preferences.SaveFolderPath,
            $"{Path.GetFileNameWithoutExtension(SubSelectedFilePath)}_sub_{SubSelectedTrack.StreamIndex}{ext}");

        var request = new ProcessSubtitleExtractRequest(
            SubSelectedFilePath,
            outputPath,
            SubSelectedTrack.StreamIndex,
            SubExportFormat);

        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        var progress = new Progress<SubtitleExtractProgressReport>(r =>
        {
            void Apply()
            {
                SubProgressPercent01 = r.Percent01;
                SubProgressDetailText = r.StepDescription;
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
                SubProgressStatusText = "Cancelled";
                SubSucceeded = false;
            }
            else if (result.IsSuccess && File.Exists(outputPath))
            {
                SubSucceeded = true;
                SubProgressStatusText = "Complete";
                SubProgressPercent01 = 1;
                var len = new FileInfo(outputPath).Length;
                SubResultMessage = $"Saved to {outputPath} ({FormatBytes(len)})";
                toastTitle = "Subtitle extracted";
                toastBody = $"{Path.GetFileName(outputPath)} · {FormatBytes(len)}";
                toastSuccess = true;
            }
            else if (result.IsSuccess)
            {
                SubSucceeded = false;
                SubProgressStatusText = "Failed";
                SubProgressDetailText = "Output file was not created.";
                SubResultMessage = SubProgressDetailText;
                toastTitle = "Subtitle extract failed";
                toastBody = SubResultMessage;
            }
            else
            {
                SubSucceeded = false;
                SubProgressStatusText = "Failed";
                SubProgressDetailText = result.ErrorMessage ?? "Unknown error";
                SubResultMessage = result.ErrorMessage ?? "Extraction failed.";
                toastTitle = "Subtitle extract failed";
                toastBody = SubResultMessage;
            }
        }
        finally
        {
            SubIsRunning = false;
            SubFinishedAttempt = true;
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
                    "Video Enhancer");
            }
        }
    }

    [RelayCommand]
    private void SubCancel() => _subtitleCts?.Cancel();

    private static string SubtitleExtensionFor(SubtitleExportFormat format) =>
        format switch
        {
            SubtitleExportFormat.SubRip => ".srt",
            SubtitleExportFormat.WebVtt => ".vtt",
            SubtitleExportFormat.Ass => ".ass",
            SubtitleExportFormat.Copy => ".sub",
            _ => ".srt"
        };

    private async Task<bool> LoadSubtitleFileAsync(string path)
    {
        SubIsAnalyzing = true;
        SubtitleTracks.Clear();
        SubSelectedTrack = null;
        try
        {
            var analysis = await _subtitleExtractorService.AnalyzeAsync(path).ConfigureAwait(true);
            SubSelectedFilePath = path;
            SubFileDisplayName = analysis.FileName;
            SubFileSizeDisplay = FormatBytes(analysis.FileSizeBytes);
            SubDurationDisplay = analysis.Duration is { } d ? FormatDuration(d) : "—";
            SubFormatDisplay = analysis.ContainerFormatHint;

            foreach (var t in analysis.SubtitleTracks)
            {
                SubtitleTracks.Add(t);
            }

            SubSelectedTrack = SubtitleTracks.FirstOrDefault();
            SubFinishedAttempt = false;
            SubSucceeded = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not read media: {ex.Message}");
            SubSelectedFilePath = null;
            SubFileDisplayName = string.Empty;
            SubFileSizeDisplay = string.Empty;
            SubDurationDisplay = string.Empty;
            SubFormatDisplay = string.Empty;
            return false;
        }
        finally
        {
            SubIsAnalyzing = false;
            ExtractSubtitleCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSubSelectedTrackChanged(SubtitleTrackInfoDto? value) =>
        ExtractSubtitleCommand.NotifyCanExecuteChanged();

    partial void OnSubSelectedFilePathChanged(string? value) =>
        ExtractSubtitleCommand.NotifyCanExecuteChanged();

    partial void OnSubIsRunningChanged(bool value)
    {
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    partial void OnSubIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        ExtractSubtitleCommand.NotifyCanExecuteChanged();
    }
}
