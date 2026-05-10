namespace MediaTools.Application.DTOs;

public sealed record SubtitleTrackInfoDto(int StreamIndex, string Codec, string Language, string Title)
{
    public string DisplayLabel
    {
        get
        {
            var lang = string.IsNullOrWhiteSpace(Language) ? "—" : Language;
            var title = string.IsNullOrWhiteSpace(Title) ? string.Empty : $" · {Title}";
            return $"{lang} · {Codec} · #{StreamIndex}{title}";
        }
    }
}
