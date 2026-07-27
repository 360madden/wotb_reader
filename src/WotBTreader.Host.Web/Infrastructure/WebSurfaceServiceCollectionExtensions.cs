using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Infrastructure;

internal static class WebSurfaceServiceCollectionExtensions
{
    public static IServiceCollection AddWebSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LocalMutationSecurity>();
        services.Configure<RendezvousOptions>(
            configuration.GetSection(RendezvousOptions.SectionName));
        services.AddHostedService<RendezvousPublisher>();
        services.AddScoped<IDashboardReadClient, DashboardReadClient>();
        services.AddSingleton<MinimapTextureService>();
        return services;
    }
}
