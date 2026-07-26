using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Discovery;

/// <summary>Finds and fingerprints a read-only local WotB installation.</summary>
public interface IGameInstallationDiscovery
{
    /// <summary>Returns the first valid installation using deterministic root precedence.</summary>
    ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
        CancellationToken cancellationToken);
}

internal sealed record GameExecutableIdentity(string ProductVersion, ContentHash Sha256);

internal interface IGameExecutableIdentityReader
{
    ValueTask<OperationResult<GameExecutableIdentity>> ReadAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

internal sealed class GameExecutableIdentityReader : IGameExecutableIdentityReader
{
    public async ValueTask<OperationResult<GameExecutableIdentity>> ReadAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
            string? productVersion = versionInfo.ProductVersion;
            if (string.IsNullOrWhiteSpace(productVersion))
            {
                productVersion = versionInfo.FileVersion;
            }

            if (string.IsNullOrWhiteSpace(productVersion))
            {
                return OperationResult.Failure<GameExecutableIdentity>(
                    new ApplicationError(
                        "game.discovery.missing_version",
                        "The game executable does not expose a product version."));
            }

            await using FileStream stream = new(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return OperationResult.Success(
                new GameExecutableIdentity(productVersion.Trim(), new ContentHash(Convert.ToHexString(hash))));
        }
        catch (FileNotFoundException)
        {
            return Failure("game.discovery.not_found", "The game executable was not found.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("game.discovery.access_denied", "The game executable could not be read.");
        }
        catch (IOException)
        {
            return Failure(
                "game.discovery.io_failure",
                "The game executable could not be fingerprinted consistently.",
                retryable: true);
        }
    }

    private static OperationResult<GameExecutableIdentity> Failure(
        string code,
        string message,
        bool retryable = false) =>
        OperationResult.Failure<GameExecutableIdentity>(
            new ApplicationError(code, message, retryable));
}

/// <summary>
/// Discovers WotB using bounded, non-recursive candidate paths. Discovery never
/// modifies an installation and never returns an identity without hashing the executable.
/// </summary>
public sealed class GameInstallationDiscovery : IGameInstallationDiscovery
{
    private const string ExecutableName = "wotblitz.exe";

    private readonly GameIntegrationOptions _options;
    private readonly IGameExecutableIdentityReader _identityReader;
    private readonly ILogger<GameInstallationDiscovery> _logger;

    /// <summary>Creates a game discovery service.</summary>
    public GameInstallationDiscovery(
        GameIntegrationOptions options,
        ILogger<GameInstallationDiscovery> logger)
        : this(options, new GameExecutableIdentityReader(), logger)
    {
    }

    internal GameInstallationDiscovery(
        GameIntegrationOptions options,
        IGameExecutableIdentityReader identityReader,
        ILogger<GameInstallationDiscovery> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identityReader);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _options = options;
        _identityReader = identityReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<OperationResult<InstalledGameIdentity>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = GetGameRoots();
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string executablePath = Path.Combine(root, ExecutableName);
            string resourceRoot = Path.Combine(root, "Data");
            if (!File.Exists(executablePath) || !Directory.Exists(resourceRoot))
            {
                continue;
            }

            OperationResult<GameExecutableIdentity> identityResult =
                await _identityReader.ReadAsync(executablePath, cancellationToken).ConfigureAwait(false);
            if (!identityResult.IsSuccess)
            {
                _logger.LogWarning(
                    new EventId(3101, "GameCandidateRejected"),
                    "A WotB installation candidate could not be fingerprinted ({ErrorCode}).",
                    identityResult.Error!.Code);
                continue;
            }

            GameExecutableIdentity executableIdentity = identityResult.Value!;
            string[] dlcRoots = GetDlcRoots();
            return OperationResult.Success(
                new InstalledGameIdentity(
                    Path.GetFullPath(executablePath),
                    executableIdentity.ProductVersion,
                    executableIdentity.Sha256,
                    Path.GetFullPath(resourceRoot),
                    dlcRoots));
        }

        return OperationResult.Failure<InstalledGameIdentity>(
            new ApplicationError(
                "game.discovery.not_found",
                "No readable WotB installation was found in the configured roots."));
    }

    private string[] GetGameRoots()
    {
        List<string> roots = [];
        roots.AddRange(_options.GameInstallRoots);

        if (_options.UseDefaultDiscoveryRoots && OperatingSystem.IsWindows())
        {
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string systemDrive = Path.GetPathRoot(systemRoot) ?? "C:\\";
            roots.Add(Path.Combine(systemDrive, "Games", "World_of_Tanks_Blitz"));

            AddIfNotEmpty(
                roots,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam",
                "steamapps",
                "common",
                "World of Tanks Blitz");
            AddIfNotEmpty(
                roots,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam",
                "steamapps",
                "common",
                "World of Tanks Blitz");
        }

        return NormalizeRoots(roots);
    }

    private string[] GetDlcRoots()
    {
        List<string> candidates = [];
        foreach (string configuredRoot in GetUserDataRoots())
        {
            string root = Path.GetFullPath(configuredRoot);
            string leaf = Path.GetFileName(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (leaf.Equals("packs", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(root);
            }
            else if (leaf.Equals("DAVAProject", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo? parent = Directory.GetParent(root);
                if (parent is not null)
                {
                    candidates.Add(Path.Combine(parent.FullName, "packs"));
                }
            }
            else
            {
                candidates.Add(Path.Combine(root, "packs"));
            }
        }

        return NormalizeRoots(candidates).Where(Directory.Exists).ToArray();
    }

    internal string[] GetUserDataRoots()
    {
        List<string> roots = [];
        roots.AddRange(_options.UserDataRoots);
        if (_options.UseDefaultDiscoveryRoots)
        {
            string localAppData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                roots.Add(Path.Combine(localAppData, "wotblitz"));
            }
        }

        return NormalizeRoots(roots);
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in roots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid configured roots are skipped; no path is logged to preserve privacy.
            }
        }

        return [.. normalized];
    }

    private static void AddIfNotEmpty(
        List<string> roots,
        string basePath,
        params string[] segments)
    {
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            roots.Add(segments.Aggregate(basePath, Path.Combine));
        }
    }
}
