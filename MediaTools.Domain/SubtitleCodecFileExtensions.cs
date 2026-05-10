namespace MediaTools.Domain;

/// <summary>Maps FFmpeg subtitle codec names to a reasonable file extension when using stream copy.</summary>
public static class SubtitleCodecFileExtensions
{
    public static string SuggestExtension(string codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return ".sub";
        }

        var c = codec.Trim().ToLowerInvariant();
        return c switch
        {
            "subrip" or "mov_text" or "text" => ".srt",
            "ass" or "ssa" => ".ass",
            "webvtt" or "vtt" => ".vtt",
            "hdmv_pgs_subtitle" or "pgssub" => ".sup",
            "dvd_subtitle" => ".sub",
            "dvb_subtitle" => ".sub",
            "xsub" => ".idx",
            "microdvd" => ".sub",
            "sami" or "srt" => ".srt",
            _ => ".sub"
        };
    }
}
