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
        services.AddSingleton<IImageProcessingService, ImageSharpPhotoProcessingService>();
        services.AddSingleton<ICompressionJobRepository, InMemoryCompressionJobRepository>();
        return services;
    }
}
