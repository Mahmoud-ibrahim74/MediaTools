namespace MediaTools.Domain.ValueObjects;

public sealed record ObjectEraserSettings(
    /// <summary>Gaussian sigma for content-aware blur fill inside brushed regions.</summary>
    float InpaintBlurSigma,
    /// <summary>0–1 brush edge softness (1 = soft).</summary>
    float BrushSoftness01);
