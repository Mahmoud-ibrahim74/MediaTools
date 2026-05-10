using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private bool _subIsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubProgressCard))]
    private bool _subFinishedAttempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSubResultCard))]
    private bool _subSucceeded;

    [ObservableProperty]
    private string _subResultMessage = string.Empty;

    public bool ShowSubFileInfoCard => !string.IsNullOrWhiteSpace(SubSelectedFilePath);

    public bool ShowSubtitleTrackPicker => ShowSubFileInfoCard && SubtitleTracks.Count > 0;

    public bool ShowNoSubtitleTracksWarning => ShowSubFileInfoCard && !SubIsAnalyzing && SubtitleTracks.Count == 0;

    public bool ShowSubProgressCard => SubIsRunning || SubFinishedAttempt;

    public bool ShowSubResultCard => SubSucceeded;

    public int SubProgressPercentDisplay => (int)Math.Round(SubProgressPercent01 * 100, MidpointRounding.AwayFromZero);

    private void OnSubtitleTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        StartCommand.NotifyCanExecuteChanged();
    }

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
        OnPropertyChanged(nameof(ShowSubtitleProgressCard));
        OnPropertyChanged(nameof(ShowSubtitleResultCard));
        StartCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(ShowSubtitleProgressCard));
        OnPropertyChanged(nameof(ShowSubtitleResultCard));
    }

    internal async Task RunSubtitleExtractAsync()
    {
        if (SubSelectedFilePath is null || SubSelectedTrack is null)
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
            StartCommand.NotifyCanExecuteChanged();
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
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSubSelectedTrackChanged(SubtitleTrackInfoDto? value) =>
        StartCommand.NotifyCanExecuteChanged();

    partial void OnSubSelectedFilePathChanged(string? value) =>
        StartCommand.NotifyCanExecuteChanged();

    partial void OnSubIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(ShowSubtitleProgressCard));
    }

    partial void OnSubFinishedAttemptChanged(bool value) =>
        OnPropertyChanged(nameof(ShowSubtitleProgressCard));

    partial void OnSubSucceededChanged(bool value) =>
        OnPropertyChanged(nameof(ShowSubtitleResultCard));

    partial void OnSubIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoSubtitleTracksWarning));
        StartCommand.NotifyCanExecuteChanged();
    }
}
