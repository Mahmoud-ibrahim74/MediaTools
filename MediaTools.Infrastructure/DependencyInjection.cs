using MediaTools.Application.Abstractions;
using MediaTools.Infrastructure.Repositories;
using MediaTools.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MediaTools.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IVideoCompressionService, FfmpegVideoCompressionService>();
        services.AddSingleton<IAudioProcessingService, FfmpegAudioProcessingService>();
        services.AddSingleton<IImageProcessingService, ImageSharpPhotoProcessingService>();
        services.AddSingleton<IThumbnailGeneratorService, ThumbnailGeneratorService>();
        services.AddSingleton<ISubtitleExtractorService, FfmpegSubtitleExtractorService>();
        services.AddSingleton<IScreenRecordingService, FfmpegScreenRecordingService>();
        services.AddSingleton<IVideoEnhanceService, FfmpegVideoEnhanceService>();
        services.AddSingleton<IVideoEncoderProbeService, FfmpegVideoEncoderProbeService>();
        services.AddSingleton<ICompressionJobRepository, InMemoryCompressionJobRepository>();
        services.AddSingleton<IYouTubeAudioService, YtDlpYouTubeAudioService>();
        services.AddSingleton<IYouTubeVideoService, YtDlpYouTubeVideoService>();
        services.AddSingleton<IFacebookVideoService, YtDlpFacebookVideoService>();
        return services;
    }
}
