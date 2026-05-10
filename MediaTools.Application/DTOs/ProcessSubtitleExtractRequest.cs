using MediaTools.Domain.Enums;

namespace MediaTools.Application.DTOs;

public sealed record ProcessSubtitleExtractRequest(
    string SourcePath,
    string OutputFilePath,
    int SubtitleStreamIndex,
    SubtitleExportFormat ExportFormat);
