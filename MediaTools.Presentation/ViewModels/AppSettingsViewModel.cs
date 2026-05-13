using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
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

        PropertyChanged += (_, _) => SaveSettingsCommand.NotifyCanExecuteChanged();
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotkeyCaptureActive))]
    [NotifyPropertyChangedFor(nameof(HotkeyCaptureHint))]
    private ScreenRecorderHotkeyCaptureSlot _hotkeyCaptureSlot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStartHotkeyDisplay))]
    private HotkeySetting _draftStartHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftPauseHotkeyDisplay))]
    private HotkeySetting _draftPauseHotkey;

    public bool IsHotkeyCaptureActive => HotkeyCaptureSlot != ScreenRecorderHotkeyCaptureSlot.None;

    public string HotkeyCaptureHint =>
        HotkeyCaptureSlot switch
        {
            ScreenRecorderHotkeyCaptureSlot.StartRecording => "Press the keys for Start recording… (Esc to cancel)",
            ScreenRecorderHotkeyCaptureSlot.PauseRecording => "Press the keys for Pause / resume… (Esc to cancel)",
            _ => string.Empty
        };

    public string DraftStartHotkeyDisplay => DraftStartHotkey.ToDisplayString();

    public string DraftPauseHotkeyDisplay => DraftPauseHotkey.ToDisplayString();

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

        DraftStartHotkey = _preferences.ScreenRecorderStartHotkey;
        DraftPauseHotkey = _preferences.ScreenRecorderPauseHotkey;
        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.None;

        SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Captures the next key press when <see cref="HotkeyCaptureSlot"/> is active (handled by App settings page).
    /// </summary>
    public bool TryConsumePreviewKeyDown(KeyEventArgs e)
    {
        if (HotkeyCaptureSlot == ScreenRecorderHotkeyCaptureSlot.None)
        {
            return false;
        }

        if (e.Key == Key.Escape)
        {
            HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.None;
            e.Handled = true;
            return true;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var hk = HotkeySetting.FromWpfKey(key, Keyboard.Modifiers);
        if (hk.IsEmpty || IsBareModifierVirtualKey(hk.VirtualKey))
        {
            e.Handled = true;
            return true;
        }

        var conflict = DescribeHotkeyConflict(HotkeyCaptureSlot, hk);
        if (conflict is not null)
        {
            MessageBoxHelper.ShowWarning(
                $"That key combination is already assigned to {conflict}. Each action needs its own shortcut (or Clear one).");
            e.Handled = true;
            return true;
        }

        switch (HotkeyCaptureSlot)
        {
            case ScreenRecorderHotkeyCaptureSlot.StartRecording:
                DraftStartHotkey = hk;
                break;
            case ScreenRecorderHotkeyCaptureSlot.PauseRecording:
                DraftPauseHotkey = hk;
                break;
            default:
                return false;
        }

        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.None;
        e.Handled = true;
        return true;
    }

    private static bool IsBareModifierVirtualKey(uint vk) =>
        vk is >= 0xA0 and <= 0xA5 || vk == 0x10 || vk == 0x11 || vk == 0x12;

    [RelayCommand]
    private void BeginCaptureStartHotkey() =>
        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.StartRecording;

    [RelayCommand]
    private void BeginCapturePauseHotkey() =>
        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.PauseRecording;

    [RelayCommand]
    private void ClearDraftStartHotkey()
    {
        DraftStartHotkey = HotkeySetting.Empty;
        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.None;
    }

    [RelayCommand]
    private void ClearDraftPauseHotkey()
    {
        DraftPauseHotkey = HotkeySetting.Empty;
        HotkeyCaptureSlot = ScreenRecorderHotkeyCaptureSlot.None;
    }

    /// <summary>Returns the name of another action that already uses this combo, or null if OK.</summary>
    private string? DescribeHotkeyConflict(ScreenRecorderHotkeyCaptureSlot assigning, HotkeySetting candidate)
    {
        if (candidate.IsEmpty)
        {
            return null;
        }

        if (assigning != ScreenRecorderHotkeyCaptureSlot.StartRecording
            && HotkeyEquals(candidate, DraftStartHotkey))
        {
            return "Start recording";
        }

        if (assigning != ScreenRecorderHotkeyCaptureSlot.PauseRecording
            && HotkeyEquals(candidate, DraftPauseHotkey))
        {
            return "Pause / resume";
        }

        return null;
    }

    private static bool HotkeyEquals(HotkeySetting a, HotkeySetting b) =>
        !a.IsEmpty && !b.IsEmpty && a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey;

    private static bool HotkeysAreDistinct(HotkeySetting a, HotkeySetting b) =>
        a.IsEmpty || b.IsEmpty || !HotkeyEquals(a, b);

    private static bool ValidateDistinctHotkeys(HotkeySetting start, HotkeySetting pause) =>
        HotkeysAreDistinct(start, pause);

    private bool CanSaveSettings()
    {
        if (!string.Equals(SaveFolderPathDraft.Trim(), _preferences.SaveFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ToastNotificationsEnabledDraft != _preferences.ToastNotificationsEnabled)
        {
            return true;
        }

        if (PreferredEncoderDraft != _preferences.PreferredVideoHardwareEncoder)
        {
            return true;
        }

        if (!EncoderScanDraftMatchesPersisted())
        {
            return true;
        }

        if (DraftStartHotkey != _preferences.ScreenRecorderStartHotkey)
        {
            return true;
        }

        if (DraftPauseHotkey != _preferences.ScreenRecorderPauseHotkey)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the in-memory encoder scan matches what was last persisted (including “never scanned”).
    /// </summary>
    private bool EncoderScanDraftMatchesPersisted()
    {
        var persisted = _preferences.LastVideoEncoderScan;
        if (persisted is null)
        {
            return !HasCompletedEncoderScan
                && DraftEncoderScan.Equals(new VideoEncoderScanResult(false, false, false));
        }

        return HasCompletedEncoderScan && DraftEncoderScan.Equals(persisted);
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

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private void SaveSettings()
    {
        var trimmed = SaveFolderPathDraft.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            MessageBoxHelper.ShowInformation("Please enter a folder path.");
            return;
        }

        if (!ValidateDistinctHotkeys(DraftStartHotkey, DraftPauseHotkey))
        {
            MessageBoxHelper.ShowWarning(
                "Screen recorder shortcuts must be unique: the same key combination cannot be used for more than one action. Change or clear a duplicate.");
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

            _preferences.SetScreenRecorderHotkeys(DraftStartHotkey, DraftPauseHotkey);

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

            DraftStartHotkey = _preferences.ScreenRecorderStartHotkey;
            DraftPauseHotkey = _preferences.ScreenRecorderPauseHotkey;

            MessageBoxHelper.ShowInformation("Settings saved.");
        }
        catch (Exception ex)
        {
            MessageBoxHelper.ShowWarning($"Could not save settings: {ex.Message}");
        }
    }
}
