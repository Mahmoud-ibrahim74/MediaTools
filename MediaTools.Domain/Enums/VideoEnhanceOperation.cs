namespace MediaTools.Domain.Enums;

public enum VideoEnhanceOperation
{
    Watermark,
    SpeedChange,
    Reverse,
    ColorGrading,
    CropAndResize,
    ExtractAudio,
    /// <summary>Embedded subtitle streams → SRT, VTT, ASS, or copy (handled outside the FFmpeg enhance graph).</summary>
    ExtractSubtitle
}
