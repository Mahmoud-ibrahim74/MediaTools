using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Application.UseCases;
using MediaTools.Domain.Enums;
using MediaTools.Domain.ValueObjects;
using MediaTools.Presentation.Services;

namespace MediaTools.Presentation.ViewModels;

public partial class AudioCompressViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".caf", ".mp4", ".mka"
    ];

    private readonly ProcessAudioUseCase _processAudioUseCase;
    private readonly IAudioProcessingService _audioProcessingService;
    private readonly IUserPreferencesService _preferences;
    private CancellationTokenSource? _compressionCts;
    private long _sourceSizeBytes;

    public AudioCompressViewModel(
        ProcessAudioUseCase processAudioUseCase,
        IAudioProcessingService audioProcessingService,
        IUserPreferencesService preferences)
    {
        _processAudioUseCase = processAudioUseCase;
        _audioProcessingService = audioProcessingService;
        _preferences = preferences;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;
    }

    public IEnumerable<AudioExportFormat> TargetFormats =>
        [AudioExportFormat.Mp3, AudioExportFormat.M4aAac, AudioExportFormat.OggOpus];

    public IEnumerable<int> BitrateOptions =>
        TargetFormat == AudioExportFormat.OggOpus
            ? [64, 96, 128, 160, 192, 256]
            : [64, 96, 128, 160, 192, 256, 320];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileInfoDisplay = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateOptions))]
    [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
    private AudioExportFormat _targetFormat = AudioExportFormat.Mp3;

    [ObservableProperty]
    private int _bitrateKbps = 128;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseFileCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isDropHover;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public bool ShowFileInfo => !string.IsNullOrWhiteSpace(SelectedFilePath);

    public bool ShowResult => !string.IsNullOrWhiteSpace(ResultText);

    partial void OnSelectedFilePathChanged(string? value) =>
        OnPropertyChanged(nameof(ShowFileInfo));

    partial void OnResultTextChanged(string value) =>
        OnPropertyChanged(nameof(ShowResult));

    partial void OnTargetFormatChanged(AudioExportFormat value)
    {
        if (value == AudioExportFormat.OggOpus && BitrateKbps > 256)
        {
            BitrateKbps = 256;
        }
    }

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        CompressCommand.NotifyCanExecuteChanged();

    private bool CanBrowse() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFileAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.caf;*.mp4;*.mka|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadFileAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CompressAsync()
    {
        if (!CanCompress() || SelectedFilePath is null)
        {
            return;
        }

        _compressionCts = new CancellationTokenSource();
        var token = _compressionCts.Token;
        IsRunning = true;
        ProgressPercent = 0;
        ResultText = string.Empty;
        StatusText = "Compressing audio…";

        var outputPath = GetUniqueOutputPath(
            _preferences.SaveFolderPath,
            Path.GetFileNameWithoutExtension(SelectedFilePath) + "_compressed",
            ExtensionFor(TargetFormat));
        var settings = new AudioEnhanceSettings(
            TargetFormat,
            BitrateKbps,
            AudioSampleRateOption.Original,
            NormalizeLoudness: false,
            VolumePercent: 100,
            ClarityBoost: false,
            IncludeVocalRemover: false,
            IncludeNoiseReduction: false,
            IncludeSilenceRemover: false,
            VocalRemoverStrength01: 0,
            NoiseReductionStrength01: 0,
            SilenceThresholdDb: -40,
            MinSilenceDurationSec: 0.5f,
            SilenceDetectionWindowSec: 0.02f);
        var progress = new Progress<AudioProgressReport>(report =>
        {
            ProgressPercent = Math.Clamp(report.Percent01, 0, 1) * 100;
            StatusText = report.StepDescription;
        });

        try
        {
            var result = await _processAudioUseCase.ExecuteAsync(
                new ProcessAudioRequest(SelectedFilePath, outputPath, settings),
                progress,
                token).ConfigureAwait(true);

            if (result.IsCancelled)
            {
                StatusText = "Compression cancelled.";
                return;
            }

            if (!result.IsSuccess || !File.Exists(outputPath))
            {
                StatusText = result.ErrorMessage ?? "The compressed audio was not created.";
                return;
            }

            ProgressPercent = 100;
            StatusText = "Compression complete.";
            var outputSize = new FileInfo(outputPath).Length;
            var sizeChange = _sourceSizeBytes == 0
                ? string.Empty
                : outputSize < _sourceSizeBytes
                    ? $" · {_sourceSizeBytes - outputSize:N0} bytes saved ({(1 - (double)outputSize / _sourceSizeBytes):P0})"
                    : outputSize == _sourceSizeBytes
                        ? " · Same file size as the original"
                        : $" · {outputSize - _sourceSizeBytes:N0} bytes larger than the original";
            ResultText = $"Saved {Path.GetFileName(outputPath)} ({FormatBytes(outputSize)}){sizeChange}\n{outputPath}";
        }
        finally
        {
            IsRunning = false;
            _compressionCts.Dispose();
            _compressionCts = null;
        }
    }

    [RelayCommand]
    private void CancelCompression() => _compressionCts?.Cancel();

    public void HandleDrop(IEnumerable<string> paths)
    {
        if (IsRunning)
        {
            return;
        }

        var path = paths.FirstOrDefault(p => AllowedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
        if (path is not null)
        {
            _ = LoadFileAsync(path);
        }
    }

    private async Task LoadFileAsync(string path)
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var audio = await _audioProcessingService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = audio.FileName;
            var bitrate = audio.BitrateKbps is { } kbps ? $"{kbps} kbps" : "Bitrate unavailable";
            FileInfoDisplay = $"{FormatBytes(audio.FileSizeBytes)} · {audio.Duration:hh\\:mm\\:ss} · {audio.Codec} · {bitrate}";
            _sourceSizeBytes = audio.FileSizeBytes;
            ResultText = string.Empty;
            StatusText = "Ready to compress.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open audio: {ex.Message}";
        }
    }

    private bool CanCompress() =>
        !IsRunning
        && !string.IsNullOrWhiteSpace(SelectedFilePath)
        && Directory.Exists(_preferences.SaveFolderPath);

    private static string ExtensionFor(AudioExportFormat format) =>
        format switch
        {
            AudioExportFormat.Mp3 => ".mp3",
            AudioExportFormat.M4aAac => ".m4a",
            AudioExportFormat.OggOpus => ".opus",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

    private static string GetUniqueOutputPath(string folder, string fileName, string extension)
    {
        var path = Path.Combine(folder, fileName + extension);
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(folder, $"{fileName}_{suffix}{extension}");
        }

        return path;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var order = 0;
        while (size >= 1024 && order < units.Length - 1)
        {
            size /= 1024;
            order++;
        }

        return $"{size:0.##} {units[order]}";
    }
}
