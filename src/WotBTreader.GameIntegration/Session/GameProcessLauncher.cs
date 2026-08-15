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
            using Process? process = Process.Start(CreateStartInfo(identity));
            if (process is null)
            {
                return OperationResult.Failure<GameProcessLaunchOutcome>(
                    new ApplicationError(
                        "game.launch.process_start_failed",
                        "The game process could not be started.",
                        Retryable: true));
            }

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

    /// <summary>
    /// Builds the normal Windows launch settings. The working directory is
    /// deliberately the installed executable's directory: the client resolves
    /// its WGC bridge and native runtime resources relative to that directory,
    /// not relative to the host process or caller's current directory.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(InstalledGameIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string? workingDirectory = Path.GetDirectoryName(identity.ExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new InvalidOperationException(
                "The installed executable has no usable working directory.");
        }

        return new ProcessStartInfo
        {
            FileName = identity.ExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
    }
}
