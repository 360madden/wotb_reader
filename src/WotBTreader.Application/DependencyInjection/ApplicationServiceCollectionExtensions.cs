using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Storage;
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
        services.AddScoped<IOverlayFrameSource, ReplayFrameSource>();
        services.AddSingleton<IOffsetTableReader>(sp =>
        {
            string offsetsPath = Path.Combine(
                AppContext.BaseDirectory, "memory-offsets");
            // Fall back to the repository root layout for development.
            if (!Directory.Exists(offsetsPath))
            {
                string? candidate = AppContext.BaseDirectory;
                for (int i = 0; i < 6 && candidate is not null; i++)
                {
                    string test = Path.Combine(candidate, "memory-offsets");
                    if (Directory.Exists(test))
                    {
                        offsetsPath = test;
                        break;
                    }

                    candidate = Path.GetDirectoryName(candidate);
                }
            }

            return new OffsetTableReader(offsetsPath);
        });
        return services;
    }
}
