using MediaTools.Domain.ValueObjects;

namespace MediaTools.Application.DTOs;

public sealed record CompressVideoRequest(string SourcePath, string OutputFilePath, CompressionProfile Profile);
