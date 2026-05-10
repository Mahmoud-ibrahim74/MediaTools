namespace MediaTools.Domain.Enums;

/// <summary>
/// Resize/interpolation profile. <see cref="AiEnhanced"/> uses Lanczos plus local tone/detail refinement (not an external cloud AI API).
/// </summary>
public enum UpscaleQualityMode
{
    None,
    HighQuality,
    AiEnhanced
}
