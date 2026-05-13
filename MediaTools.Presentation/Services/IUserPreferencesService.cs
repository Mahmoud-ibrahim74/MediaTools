using MediaTools.Application.DTOs;
using MediaTools.Domain.Enums;

namespace MediaTools.Presentation.Services;

public interface IUserPreferencesService
{
    /// <summary>Folder where Video Compress and Photo Enhancer write output files.</summary>
    string SaveFolderPath { get; }

    /// <summary>When false, completion toasts are not shown (in-app messages still appear).</summary>
    bool ToastNotificationsEnabled { get; }

    /// <summary>Preferred H.264 encoder for Video tools export.</summary>
    VideoHardwareEncoderKind PreferredVideoHardwareEncoder { get; }

    /// <summary>Last FFmpeg probe result; null if the user has never saved a scan.</summary>
    VideoEncoderScanResult? LastVideoEncoderScan { get; }

    void SetSaveFolderPath(string path);

    void SetToastNotificationsEnabled(bool enabled);

    /// <summary>
    /// Persists encoder preference and optional new scan. Coerces preference to Software if it does not match scan.
    /// </summary>
    void SetVideoEncoderSettings(VideoHardwareEncoderKind preference, VideoEncoderScanResult scan);

    event EventHandler? SaveFolderPathChanged;

    event EventHandler? VideoEncoderSettingsChanged;

    /// <summary>Global shortcut to start recording when not recording (see App settings).</summary>
    HotkeySetting ScreenRecorderStartHotkey { get; }

    /// <summary>Global shortcut to pause / resume while recording.</summary>
    HotkeySetting ScreenRecorderPauseHotkey { get; }

    void SetScreenRecorderHotkeys(HotkeySetting start, HotkeySetting pause);

    event EventHandler? ScreenRecorderHotkeysChanged;

    /// <summary>Persisted count of successful video compressions (increment on each saved export).</summary>
    int LifetimeVideoCompressedCount { get; }

    /// <summary>Persisted count of successful photo enhancements / exports from Photo Enhancer.</summary>
    int LifetimePhotoEnhancedCount { get; }

    /// <summary>Persisted count of successful audio exports.</summary>
    int LifetimeAudioEnhancedCount { get; }

    /// <summary>Persisted count of screen recordings saved to disk.</summary>
    int LifetimeScreenRecordedCount { get; }

    /// <summary>Increments a lifetime counter and saves settings.</summary>
    void IncrementLifetimeStat(AppLifetimeStatKind kind);

    event EventHandler? LifetimeStatsChanged;
}
