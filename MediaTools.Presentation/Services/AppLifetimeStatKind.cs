namespace MediaTools.Presentation.Services;

/// <summary>Lifetime completion counters persisted with user settings (incremented when an export/recording saves successfully).</summary>
public enum AppLifetimeStatKind
{
    VideoCompressed,
    PhotoEnhanced,
    AudioEnhanced,
    ScreenRecorded
}
