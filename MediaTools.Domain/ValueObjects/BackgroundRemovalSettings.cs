using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record BackgroundRemovalSettings(
    BackgroundRemovalMode Mode,
    /// <summary>0–100; scales RGB distance threshold for auto edge / chroma.</summary>
    int Tolerance,
    /// <summary>Gaussian sigma applied to the removal mask for softer edges (0 = sharp).</summary>
    float FeatherSigma,
    /// <summary>Chroma key RGB (ignored when not using <see cref="BackgroundRemovalMode.ChromaKey"/>).</summary>
    byte KeyR,
    byte KeyG,
    byte KeyB,
    /// <summary>0–1; pixels lighter than this become transparent in luminance mode.</summary>
    float LuminanceThreshold01,
    /// <summary>Expand removal slightly after classification (morphological dilation in px).</summary>
    int EdgeExpandPx);
