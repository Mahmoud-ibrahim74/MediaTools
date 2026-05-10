namespace MediaTools.Domain.Enums;

public enum PhotoResizeIntent
{
    /// <summary>Keep pixel dimensions (still re-encoded / filtered).</summary>
    Original,

    /// <summary>Multiply width and height by ScaleFactor (for upscale/downscale).</summary>
    ScaleByFactor,

    /// <summary>Fit inside a square / max edge while preserving aspect ratio.</summary>
    FitMaxEdge
}
