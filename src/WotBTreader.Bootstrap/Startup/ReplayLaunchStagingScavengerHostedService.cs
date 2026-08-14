using Microsoft.Extensions.Hosting;
using WotBTreader.GameIntegration;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.Bootstrap.Startup;

/// <summary>
/// Removes orphaned managed-launch staging files at host startup — the
/// hard-kill recovery the graceful lease-dispose path cannot perform. A host
/// killed before its launch lease was disposed leaves GUID stage files and
/// flat GUID clones behind, and a host that starts but never launches would
/// otherwise never trigger the stager's own scavenge. Best-effort and safe:
/// only GUID-named <c>.wotbreplay</c> temp files are removed, never originals.
/// </summary>
internal sealed class ReplayLaunchStagingScavengerHostedService(
    GameIntegrationOptions options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplayLaunchStagingScavenger.Scavenge(options.ReplayLaunchStagingRoot);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
