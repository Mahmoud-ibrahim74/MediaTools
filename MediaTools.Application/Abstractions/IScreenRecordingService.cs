using MediaTools.Application.DTOs;
using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.Abstractions;

public interface IScreenRecordingService
{
    /// <summary>List DirectShow audio capture devices visible to FFmpeg (microphones, line-in, etc.).</summary>
    Task<IReadOnlyList<AudioInputDeviceDto>> GetAudioInputDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Run an interactive screen recording session. The task completes when <paramref name="stopSignal"/>
    /// is signalled (graceful stop) or <paramref name="cancellationToken"/> is cancelled (hard cancel).
    /// </summary>
    Task<ScreenRecordingResult> RecordAsync(
        string outputPath,
        ScreenRecordingSettings settings,
        IProgress<ScreenRecordingProgressReport> progress,
        CancellationToken stopSignal,
        CancellationToken cancellationToken = default);
}
