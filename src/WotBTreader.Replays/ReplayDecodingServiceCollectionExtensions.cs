using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

/// <summary>
/// Registers the bounded replay probe and the explicit WotB 11.18 decoder.
/// </summary>
public static class ReplayDecodingServiceCollectionExtensions
{
    public static IServiceCollection AddReplayDecoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<WotbReplayProbe>();
        services.TryAddSingleton<IReplayProbe>(
            static provider => provider.GetRequiredService<WotbReplayProbe>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IReplayDecoder, WotbReplayDecoder>());
        return services;
    }
}
