namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Removes orphaned managed-launch staging leftovers. A host killed before a
/// <see cref="ManagedReplayArtifactLease"/> is disposed leaves two artifacts
/// behind: the GUID-named stage file under <c>wotbtreader-staging/</c> and the
/// flat GUID clone the game may have copied into the parent <c>replays/</c>
/// folder. The dispose path cleans both on a graceful exit; this scavenger is
/// the recovery for the hard-kill case, so duplicates never accumulate.
/// </summary>
public static class ReplayLaunchStagingScavenger
{
    /// <summary>
    /// Best-effort removal of every orphaned GUID-named stage file under
    /// <paramref name="stagingRoot"/> and, when that root is the game's
    /// <c>replays/wotbtreader-staging</c> folder, the flat GUID clones in the
    /// parent <c>replays</c> folder. Human-named originals are never touched
    /// (only 32-hex <c>.wotbreplay</c> names qualify). A file locked by an
    /// active launch is left for the next pass.
    /// </summary>
    public static void Scavenge(string? stagingRoot)
    {
        // Best-effort by contract: a scavenge must never fail the launch that
        // triggered it, so unexpected failures are swallowed here rather than
        // propagated to the caller.
        try
        {
            ScavengeCore(stagingRoot);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            // A locked or unreadable directory is left for the next pass.
        }
    }

    private static void ScavengeCore(string? stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            return;
        }

        string root = Path.GetFullPath(stagingRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        DeleteGuidStageFiles(root);

        string? parent = Path.GetDirectoryName(root);
        string folderName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(root));
        if (parent is not null
            && folderName.Equals(
                ReplayLaunchStagingPaths.StagingFolderName,
                StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent))
        {
            DeleteGuidStageFiles(parent);
        }
    }

    private static void DeleteGuidStageFiles(string directory)
    {
        // Directory.GetFiles materializes the listing before any deletion, so
        // removing a file cannot perturb the enumeration mid-pass.
        string[] files = Directory.GetFiles(
            directory,
            "*.wotbreplay",
            SearchOption.TopDirectoryOnly);

        foreach (string file in files)
        {
            if (ReplayLaunchStagingPaths.IsGuidStageFileName(Path.GetFileName(file)))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
        {
            // Best-effort only; a locked in-use clone is left for the next pass.
        }
    }
}
