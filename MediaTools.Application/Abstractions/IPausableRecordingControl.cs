namespace MediaTools.Application.Abstractions;

/// <summary>
/// Allows pausing and resuming an in-progress screen recording (implementation freezes the encoder process).
/// </summary>
public interface IPausableRecordingControl
{
    bool IsPaused { get; }

    void Pause();

    void Resume();
}
