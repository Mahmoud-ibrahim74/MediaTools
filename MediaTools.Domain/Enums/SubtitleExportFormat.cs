namespace MediaTools.Domain.Enums;

public enum SubtitleExportFormat
{
    /// <summary>SubRip (.srt). Re-encodes when the source is not already SubRip.</summary>
    SubRip,

    /// <summary>WebVTT (.vtt).</summary>
    WebVtt,

    /// <summary>ASS/SSA (.ass).</summary>
    Ass,

    /// <summary>Copy subtitle bitstream without re-encoding. Output extension follows the source codec.</summary>
    Copy
}
