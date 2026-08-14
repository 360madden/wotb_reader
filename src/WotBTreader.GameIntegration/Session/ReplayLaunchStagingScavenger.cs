namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Removes orphaned managed-launch staging leftovers. A host killed before a
/// <see cref="ManagedReplayArtifactLease"/> is disposed leaves two artifacts
/// behind: the GUID-named stage file under <c>wotbtreader-staging/</c> and the
/// flat GUID clone the game may have copied into the parent <c>replays/</c>
/// folder. The dispose path cleans both on a graceful exit; this scavenger is
/// the recovery for the hard-kill case, so duplicates never accumulate.
/// </summary>
internal static class ReplayLaunchStagingScavenger
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
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            return;
        }

        string root;
        try
        {
            root = Path.GetFullPath(stagingRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return;
        }

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
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                directory,
                "*.wotbreplay",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
        {
            return;
        }

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
