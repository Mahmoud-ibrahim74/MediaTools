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

public partial class ImageCompressViewModel : ObservableObject
{
    private static readonly HashSet<string> AllowedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"
    ];

    private readonly ProcessPhotoUseCase _processPhotoUseCase;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IUserPreferencesService _preferences;
    private CancellationTokenSource? _compressionCts;
    private long _sourceSizeBytes;

    public ImageCompressViewModel(
        ProcessPhotoUseCase processPhotoUseCase,
        IImageProcessingService imageProcessingService,
        IUserPreferencesService preferences)
    {
        _processPhotoUseCase = processPhotoUseCase;
        _imageProcessingService = imageProcessingService;
        _preferences = preferences;
        _preferences.SaveFolderPathChanged += OnSaveFolderPathChanged;
    }

    public IEnumerable<RasterImageFormat> TargetFormats =>
        [RasterImageFormat.Jpeg, RasterImageFormat.Webp, RasterImageFormat.Png];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
    private string? _selectedFilePath;

    [ObservableProperty]
    private string _fileDisplayName = string.Empty;

    [ObservableProperty]
    private string _fileInfoDisplay = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompressCommand))]
    private RasterImageFormat _targetFormat = RasterImageFormat.Webp;

    [ObservableProperty]
    private int _quality = 80;

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

    private void OnSaveFolderPathChanged(object? sender, EventArgs e) =>
        CompressCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseFileAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.gif|All files|*.*"
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
        StatusText = "Compressing image…";

        var outputPath = GetUniqueOutputPath(
            _preferences.SaveFolderPath,
            Path.GetFileNameWithoutExtension(SelectedFilePath) + "_compressed",
            ExtensionFor(TargetFormat));
        var settings = new PhotoEnhanceSettings(
            TargetFormat,
            Quality,
            PhotoResizeIntent.Original,
            1,
            null,
            UpscaleQualityMode.None,
            PhotoFilterKind.None);
        var progress = new Progress<PhotoProgressReport>(report =>
        {
            ProgressPercent = Math.Clamp(report.Percent01, 0, 1) * 100;
            StatusText = report.StepDescription;
        });

        try
        {
            var result = await _processPhotoUseCase.ExecuteAsync(
                new ProcessPhotoRequest(SelectedFilePath, outputPath, settings),
                progress,
                token).ConfigureAwait(true);

            if (result.IsCancelled)
            {
                StatusText = "Compression cancelled.";
                return;
            }

            if (!result.IsSuccess || !File.Exists(outputPath))
            {
                StatusText = result.ErrorMessage ?? "The compressed image was not created.";
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
            var image = await _imageProcessingService.AnalyzeAsync(path).ConfigureAwait(true);
            SelectedFilePath = path;
            FileDisplayName = image.FileName;
            FileInfoDisplay = $"{FormatBytes(image.FileSizeBytes)} · {image.Width} × {image.Height} · {image.FormatHint}";
            _sourceSizeBytes = image.FileSizeBytes;
            ResultText = string.Empty;
            StatusText = "Ready to compress.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open image: {ex.Message}";
        }
    }

    private bool CanCompress() =>
        !IsRunning
        && !string.IsNullOrWhiteSpace(SelectedFilePath)
        && Directory.Exists(_preferences.SaveFolderPath);

    private bool CanBrowse() => !IsRunning;

    private static string ExtensionFor(RasterImageFormat format) =>
        format switch
        {
            RasterImageFormat.Jpeg => ".jpg",
            RasterImageFormat.Webp => ".webp",
            RasterImageFormat.Png => ".png",
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
