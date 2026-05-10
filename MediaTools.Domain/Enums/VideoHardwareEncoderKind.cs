namespace MediaTools.Domain.Enums;

/// <summary>H.264 video encoder used for Video tools export (and related MP4 outputs).</summary>
public enum VideoHardwareEncoderKind
{
    /// <summary>libx264 CPU encoder — always available.</summary>
    Software = 0,

    Nvenc,
    Amf,
    QuickSync
}
