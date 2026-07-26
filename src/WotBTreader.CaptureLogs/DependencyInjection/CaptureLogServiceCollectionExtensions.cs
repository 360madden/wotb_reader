using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Capture;
using WotBTreader.CaptureLogs.Clock;
using WotBTreader.CaptureLogs.Comparison;
using WotBTreader.CaptureLogs.Ndjson;
using WotBTreader.CaptureLogs.Normalization;

namespace WotBTreader.CaptureLogs.DependencyInjection;

public static class CaptureLogServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureLogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ITelemetrySource, NdjsonTelemetrySource>();
        services.AddSingleton<ITelemetryNormalizer, TelemetryNormalizer>();
        services.AddSingleton<ITelemetryComparator, TelemetryComparator>();
        services.AddSingleton<IReplayClockSource, SegmentedReplayClockSource>();
        services.AddSingleton<ITelemetryCaptureWriter, NdjsonTelemetryWriter>();
        return services;
    }
}
