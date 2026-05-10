namespace MediaTools.Application.DTOs;

/// <summary>
/// Whether each H.264 GPU encoder works on <em>this machine</em> (short encode probe), not merely whether FFmpeg was built with the codec.
/// Software (libx264) is always available separately.
/// </summary>
public sealed record VideoEncoderScanResult(bool NvencAvailable, bool AmfAvailable, bool QuickSyncAvailable);
