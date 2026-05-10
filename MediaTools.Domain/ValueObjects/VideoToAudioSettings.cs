using MediaTools.Domain.Enums;

namespace MediaTools.Domain.ValueObjects;

public sealed record VideoToAudioSettings(AudioExportFormat Format, int BitrateKbps);
