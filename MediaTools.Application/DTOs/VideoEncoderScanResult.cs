namespace MediaTools.Application.DTOs;

/// <summary>FFmpeg-reported availability of H.264 hardware encoders (software is always implied).</summary>
public sealed record VideoEncoderScanResult(bool NvencAvailable, bool AmfAvailable, bool QuickSyncAvailable);
