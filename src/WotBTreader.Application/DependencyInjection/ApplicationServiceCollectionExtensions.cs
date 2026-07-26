using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Streaming;

namespace WotBTreader.Application.DependencyInjection;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddWotBTreaderApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ReplayDecoderRegistry>();
        services.AddSingleton<ITelemetryEventPublisher, SequencedTelemetryEventPublisher>();
        services.AddScoped<IReplayIngestionService, ReplayIngestionService>();
        return services;
    }
}
