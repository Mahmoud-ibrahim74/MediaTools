using MediaTools.Application.DTOs;

namespace MediaTools.Application.Abstractions;

public interface IVideoEncoderProbeService
{
    Task<VideoEncoderScanResult> ProbeAsync(CancellationToken cancellationToken = default);
}
