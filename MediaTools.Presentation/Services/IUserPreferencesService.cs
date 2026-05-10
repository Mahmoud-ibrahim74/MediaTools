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
}
