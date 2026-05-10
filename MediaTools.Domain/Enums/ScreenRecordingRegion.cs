namespace MediaTools.Domain.Enums;

public enum ScreenRecordingRegion
{
    /// <summary>Whole virtual desktop (every monitor combined).</summary>
    FullDesktop,

    /// <summary>Primary monitor only.</summary>
    PrimaryMonitor,

    /// <summary>User-specified rectangle.</summary>
    Custom
}
