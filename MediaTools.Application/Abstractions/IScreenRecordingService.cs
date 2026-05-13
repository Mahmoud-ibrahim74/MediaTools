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
    /// <param name="onRecordingStarted">
    /// Invoked synchronously after FFmpeg starts so the caller can pause/resume via <see cref="IPausableRecordingControl"/>.
    /// </param>
    Task<ScreenRecordingResult> RecordAsync(
        string outputPath,
        ScreenRecordingSettings settings,
        IProgress<ScreenRecordingProgressReport> progress,
        CancellationToken stopSignal,
        CancellationToken cancellationToken = default,
        Action<IPausableRecordingControl>? onRecordingStarted = null);
}
