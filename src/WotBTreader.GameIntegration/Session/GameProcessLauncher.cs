using System.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.GameIntegration.Discovery;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Starts the installed WoT Blitz executable as a plain process.
/// No replay, no suspension, no correlation — just a reliable launch.
/// </summary>
internal sealed class GameProcessLauncher : IGameProcessLauncher
{
    private readonly IGameInstallationDiscovery _discovery;

    public GameProcessLauncher(IGameInstallationDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public async ValueTask<OperationResult<GameProcessLaunchOutcome>> LaunchAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        OperationResult<InstalledGameIdentity> discoveryResult =
            await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (!discoveryResult.IsSuccess)
        {
            return OperationResult.Failure<GameProcessLaunchOutcome>(discoveryResult.Error!);
        }

        InstalledGameIdentity identity = discoveryResult.Value!;

        try
        {
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = identity.ExecutablePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            })!;

            return OperationResult.Success(
                new GameProcessLaunchOutcome(process.Id, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException)
        {
            return OperationResult.Failure<GameProcessLaunchOutcome>(
                new ApplicationError(
                    "game.launch.process_start_failed",
                    $"Could not start the game process: {exception.Message}",
                    Retryable: true));
        }
    }
}
