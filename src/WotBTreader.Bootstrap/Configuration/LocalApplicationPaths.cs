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

        return new LocalApplicationPaths(
            root,
            Path.Combine(root, "content"),
            Path.Combine(root, "treader.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "diagnostics"),
            Path.Combine(root, "rendezvous"));
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ContentStore);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Diagnostics);
        Directory.CreateDirectory(Rendezvous);
    }
}

public sealed record TreaderBootstrapOptions(
    string? ApplicationDataRoot = null,
    string? GameRoot = null,
    string? GameUserDataRoot = null);
