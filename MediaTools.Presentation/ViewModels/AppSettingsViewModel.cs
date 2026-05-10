using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaTools.Application.Abstractions;
using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;
using MediaTools.Presentation.Helpers;
using MediaTools.Presentation.Services;
using Ookii.Dialogs.Wpf;

namespace MediaTools.Presentation.ViewModels;

public partial class AppSettingsViewModel : ObservableObject
{
    private readonly IUserPreferencesService _preferences;
    private readonly IVideoEncoderProbeService _encoderProbe;
    private bool _syncingEncoderSelection;

    public AppSettingsViewModel(IUserPreferencesService preferences, IVideoEncoderProbeService encoderProbe)
    {
        _preferences = preferences;
        _encoderProbe = encoderProbe;
        EncoderRows =
        [
            new HardwareEncoderRowViewModel
            {
                Kind = VideoHardwareEncoderKind.Nvenc,
                Title = "NVENC",
                FullLabel = "NVIDIA GPU Encoder",
                Description =
                    "Best for NVIDIA GPUs (GTX 900+ / RTX series). Very fast, low CPU usage."
            },
            new HardwareEncoderRowViewModel
            {
                Kind = VideoHardwareEncoderKind.Amf,
                Title = "AMF",
                FullLabel = "AMD GPU Encoder",
                Description = "Best for AMD Radeon GPUs (RX 400+). Low latency encoding."
            },
            new HardwareEncoderRowViewModel
            {
                Kind = VideoHardwareEncoderKind.QuickSync,
                Title = "QuickSync",
                FullLabel = "Intel GPU Encoder",
                Description = "Best for Intel integrated graphics (6th gen+). Efficient and fast."
            },
            new HardwareEncoderRowViewModel
            {
                Kind = VideoHardwareEncoderKind.Software,
                Title = "Software (libx264)",
                FullLabel = "CPU fallback, always available",
                Description = "Uses CPU. Compatible with all machines. Higher CPU usage."
            }
        ];
    }

    public ObservableCollection<HardwareEncoderRowViewModel> EncoderRows { get; }

    [ObservableProperty]
    private HardwareEncoderRowViewModel? _selectedEncoderRow;

    [ObservableProperty]
    private string _saveFolderPathDraft = string.Empty;

    [ObservableProperty]
    private bool _toastNotificationsEnabledDraft = true;

    [ObservableProperty]
    private VideoHardwareEncoderKind _preferredEncoderDraft = VideoHardwareEncoderKind.Software;

    [ObservableProperty]
    private VideoEncoderScanResult _draftEncoderScan = new(false, false, false);

    [ObservableProperty]
    private bool _hasCompletedEncoderScan;

    [ObservableProperty]
    private bool _isDetectingEncoders;

    [ObservableProperty]
    private bool _showNoGpuEncoderWarning;

    public void RefreshFromPreferences()
    {
        SaveFolderPathDraft = _preferences.SaveFolderPath;
        ToastNotificationsEnabledDraft = _preferences.ToastNotificationsEnabled;
        PreferredEncoderDraft = _preferences.PreferredVideoHardwareEncoder;

        if (_preferences.LastVideoEncoderScan is { } scan)
        {
            HasCompletedEncoderScan = true;
            DraftEncoderScan = scan;
            ApplyScanToRows(scan);
        }
        else
        {
            HasCompletedEncoderScan = false;
            DraftEncoderScan = new VideoEncoderScanResult(false, false, false);
            ApplyScanToRows(null);
        }

        ApplyListSelectionFromDraft();
        UpdateGpuWarning();
    }

    private void ApplyScanToRows(VideoEncoderScanResult? scan)
    {
        foreach (var row in EncoderRows)
        {
            switch (row.Kind)
            {
                case VideoHardwareEncoderKind.Nvenc:
                    row.IsAvailable = scan?.NvencAvailable == true;
                    break;
                case VideoHardwareEncoderKind.Amf:
                    row.IsAvailable = scan?.AmfAvailable == true;
                    break;
                case VideoHardwareEncoderKind.QuickSync:
                    row.IsAvailable = scan?.QuickSyncAvailable == true;
                    break;
                default:
                    row.IsAvailable = true;
                    break;
            }
        }
    }

    private void SyncRowSelection(VideoHardwareEncoderKind kind)
    {
        PreferredEncoderDraft = kind;
        ApplyListSelectionFromDraft();
    }

    private void ApplyListSelectionFromDraft()
    {
        _syncingEncoderSelection = true;
        try
        {
            SelectedEncoderRow = EncoderRows.FirstOrDefault(r => r.Kind == PreferredEncoderDraft && r.IsAvailable)
                ?? EncoderRows.First(r => r.Kind == VideoHardwareEncoderKind.Software);
        }
        finally
        {
            _syncingEncoderSelection = false;
        }
    }

    partial void OnSelectedEncoderRowChanged(HardwareEncoderRowViewModel? value)
    {
        if (_syncingEncoderSelection || value is null || !value.IsAvailable)
        {
            return;
        }

        PreferredEncoderDraft = value.Kind;
    }

    private void UpdateGpuWarning()
    {
        ShowNoGpuEncoderWarning = HasCompletedEncoderScan
            && !DraftEncoderScan.NvencAvailable
            && !DraftEncoderScan.AmfAvailable
            && !DraftEncoderScan.QuickSyncAvailable;
    }

    private bool CanDetectEncoders() => !IsDetectingEncoders;

    [RelayCommand(CanExecute = nameof(CanDetectEncoders))]
    private async Task DetectEncodersAsync()
    {
        IsDetectingEncoders = true;
        try
        {
            var scan = await _encoderProbe.ProbeAsync(CancellationToken.None).ConfigureAwait(true);
            HasCompletedEncoderScan = true;
            DraftEncoderScan = scan;
            ApplyScanToRows(scan);

            var noGpu = !scan.NvencAvailable && !scan.AmfAvailable && !scan.QuickSyncAvailable;
            if (noGpu)
            {
                SyncRowSelection(VideoHardwareEncoderKind.Software);
            }
            else if (PreferredEncoderDraft != VideoHardwareEncoderKind.Software)
            {
                var prefOk = PreferredEncoderDraft switch
                {
                    VideoHardwareEncoderKind.Nvenc => scan.NvencAvailable,
                    VideoHardwareEncoderKind.Amf => scan.AmfAvailable,
                    VideoHardwareEncoderKind.QuickSync => scan.QuickSyncAvailable,
                    _ => true
                };
                if (!prefOk)
                {
                    PreferredEncoderDraft = VideoHardwareEncoderKind.Software;
                    SyncRowSelection(PreferredEncoderDraft);
                }
                else
                {
                    ApplyListSelectionFromDraft();
                }
            }
            else
            {
                ApplyListSelectionFromDraft();
            }

            UpdateGpuWarning();
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Encoder scan failed: {ex.Message}");
        }
        finally
        {
            IsDetectingEncoders = false;
        }
    }

    partial void OnIsDetectingEncodersChanged(bool value) =>
        DetectEncodersCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void BrowseSaveFolder()
    {
        var dlg = new VistaFolderBrowserDialog
        {
            SelectedPath = Directory.Exists(SaveFolderPathDraft.Trim())
                ? Path.GetFullPath(SaveFolderPathDraft.Trim())
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            UseDescriptionForTitle = true,
            Description = "Choose folder for compressed videos and enhanced photos"
        };

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            return;
        }

        SaveFolderPathDraft = dlg.SelectedPath;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var trimmed = SaveFolderPathDraft.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            MessageBoxHelper.ShowInformation("Please enter a folder path.");
            return;
        }

        try
        {
            _preferences.SetSaveFolderPath(trimmed);
            _preferences.SetToastNotificationsEnabled(ToastNotificationsEnabledDraft);

            var scanToSave = HasCompletedEncoderScan
                ? DraftEncoderScan
                : _preferences.LastVideoEncoderScan ?? new VideoEncoderScanResult(false, false, false);
            _preferences.SetVideoEncoderSettings(PreferredEncoderDraft, scanToSave);

            SaveFolderPathDraft = _preferences.SaveFolderPath;
            ToastNotificationsEnabledDraft = _preferences.ToastNotificationsEnabled;
            PreferredEncoderDraft = _preferences.PreferredVideoHardwareEncoder;
            if (_preferences.LastVideoEncoderScan is { } persisted)
            {
                DraftEncoderScan = persisted;
                ApplyScanToRows(persisted);
            }

            HasCompletedEncoderScan = _preferences.LastVideoEncoderScan is not null;
            ApplyListSelectionFromDraft();
            UpdateGpuWarning();

            MessageBoxHelper.ShowInformation("Settings saved.");
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not save settings: {ex.Message}");
        }
    }
}
