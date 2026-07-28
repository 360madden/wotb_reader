namespace WotBTreader.Bootstrap.Configuration;

public sealed record LocalApplicationPaths(
    string Root,
    string ContentStore,
    string Database,
    string Logs,
    string Diagnostics,
    string Rendezvous)
{
    public static LocalApplicationPaths Create(string? rootOverride = null)
    {
        string root = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WotBTreader")
            : Path.GetFullPath(rootOverride);

        // Rendezvous is ephemeral coordination data that must always live in a
        // per-user location. Putting it under a custom data root (which may be
        // shared, removable, or admin-owned) creates ACL hazards. Hardcoding to
        // %LocalAppData% avoids the entire class of permission bricking bugs.
        string rendezvous = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WotBTreader",
            "rendezvous");

        return new LocalApplicationPaths(
            root,
            Path.Combine(root, "content"),
            Path.Combine(root, "treader.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "diagnostics"),
            rendezvous);
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ContentStore);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Diagnostics);
        EnsureRendezvousDirectory();
    }

    /// <summary>
    /// Creates or re-secures the rendezvous directory so only the current user
    /// may read it. Callers that may run after the directory was removed must
    /// use this instead of <see cref="Directory.CreateDirectory(string)"/>, which
    /// would silently restore inherited permissions.
    /// </summary>
    /// <remarks>
    /// The rendezvous path is always under %LocalAppData%, which Windows
    /// already isolates per user. Custom ACL manipulation was removed because
    /// it caused permission bricking when the directory was created by an
    /// elevated process. Standard inherited permissions are sufficient for
    /// a local-only, loopback-bound tool.
    /// </remarks>
    public void EnsureRendezvousDirectory() => Directory.CreateDirectory(Rendezvous);
}

public sealed record TreaderBootstrapOptions(
    string? ApplicationDataRoot = null,
    string? GameRoot = null,
    string? GameUserDataRoot = null);
