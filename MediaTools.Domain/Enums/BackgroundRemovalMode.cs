namespace MediaTools.Domain.Enums;

/// <summary>How pixels are classified as background when removing a backdrop.</summary>
public enum BackgroundRemovalMode
{
    /// <summary>Flood-fill transparency inward from image edges using corner color similarity.</summary>
    AutoEdge,

    /// <summary>Remove pixels similar to a chosen key color (typical green screen).</summary>
    ChromaKey,

    /// <summary>Remove bright highlights — useful for white studio backgrounds.</summary>
    Luminance,
}
