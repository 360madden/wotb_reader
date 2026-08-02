namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Resolves where managed launch writes temporary <c>.wotbreplay</c> copies.
/// Prefer a dedicated subdirectory under the game's replays folder so originals
/// stay uncluttered; fall back to the app data <c>launch</c> directory when the
/// game user-data tree is absent (CI / machines without Blitz).
/// </summary>
public static class ReplayLaunchStagingPaths
{
    /// <summary>
    /// Folder name under <c>DAVAProject/replays</c> for Treader-owned stage files.
    /// Kept out of the flat Recent/Uploaded listing when the game only indexes
    /// the top-level replays directory.
    /// </summary>
    public const string StagingFolderName = "wotbtreader-staging";

    /// <summary>
    /// Returns <c>{userData}/DAVAProject/replays/wotbtreader-staging</c> when the
    /// Blitz user-data tree is present; otherwise <see langword="null"/>.
    /// </summary>
    public static string? TryResolveGameReplaysStagingRoot(string? gameUserDataRoot)
    {
        string userData = string.IsNullOrWhiteSpace(gameUserDataRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wotblitz")
            : Path.GetFullPath(gameUserDataRoot);

        string replays = Path.Combine(userData, "DAVAProject", "replays");
        if (!Directory.Exists(replays) && !Directory.Exists(userData))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(replays, StagingFolderName));
    }

    /// <summary>
    /// Game-folder staging when available; otherwise <c>{applicationDataRoot}/launch</c>.
    /// </summary>
    public static string Resolve(string? gameUserDataRoot, string applicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        return TryResolveGameReplaysStagingRoot(gameUserDataRoot)
            ?? Path.GetFullPath(Path.Combine(applicationDataRoot, "launch"));
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is a 32-hex GUID stage name
    /// (<c>{Guid:N}.wotbreplay</c>).
    /// </summary>
    public static bool IsGuidStageFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        const string extension = ".wotbreplay";
        if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> stem = fileName.AsSpan(
            0,
            fileName.Length - extension.Length);
        if (stem.Length != 32)
        {
            return false;
        }

        foreach (char c in stem)
        {
            bool hex = (c is >= '0' and <= '9')
                || (c is >= 'a' and <= 'f')
                || (c is >= 'A' and <= 'F');
            if (!hex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// When staging lives under <c>…/replays/wotbtreader-staging/{guid}.wotbreplay</c>,
    /// returns the flat sibling <c>…/replays/{guid}.wotbreplay</c> the game may have
    /// copied into Recent. Otherwise <see langword="null"/>.
    /// </summary>
    public static string? TryGetFlatReplayClonePath(string stagingFilePath)
    {
        if (string.IsNullOrWhiteSpace(stagingFilePath))
        {
            return null;
        }

        string full = Path.GetFullPath(stagingFilePath);
        string? stagingDir = Path.GetDirectoryName(full);
        if (stagingDir is null)
        {
            return null;
        }

        string folderName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(stagingDir));
        if (!folderName.Equals(StagingFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string fileName = Path.GetFileName(full);
        if (!IsGuidStageFileName(fileName))
        {
            return null;
        }

        string? replaysDir = Path.GetDirectoryName(stagingDir);
        if (replaysDir is null)
        {
            return null;
        }

        return Path.Combine(replaysDir, fileName);
    }
}
