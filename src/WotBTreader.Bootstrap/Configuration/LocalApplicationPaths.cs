using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

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
        EnsureRendezvousDirectory();
    }

    /// <summary>
    /// Creates or re-secures the rendezvous directory so only the current user
    /// may read it. Callers that may run after the directory was removed must
    /// use this instead of <see cref="Directory.CreateDirectory(string)"/>, which
    /// would silently restore inherited permissions.
    /// </summary>
    /// <remarks>
    /// When the directory was created by a different security principal (e.g. an
    /// elevated admin), the current user may lack the WriteDAC right needed to
    /// modify the ACL. In that case the existing ACL is left in place; the caller
    /// relies on the eventual file write to surface any access problem at the
    /// point of use rather than crashing the entire CLI during startup.
    /// </remarks>
    public void EnsureRendezvousDirectory() => EnsureOwnerOnlyDirectory(Rendezvous);

    /// <summary>
    /// The rendezvous record carries a live mutation capability, so inheriting a
    /// permissive parent ACL from a custom data root would hand that credential
    /// to every other local account.
    /// </summary>
    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsOwnerOnlyDirectory(path);
            return;
        }

        Directory.CreateDirectory(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwnerOnlyDirectory(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        DirectorySecurity security = new();

        // Inheritance is severed so a permissive parent cannot re-grant access,
        // and the single allow rule covers files created inside the directory.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        DirectoryInfo directory = new(path);
        if (directory.Exists)
        {
            try
            {
                directory.SetAccessControl(security);
            }
            catch (UnauthorizedAccessException)
            {
                // Silently ignore. If the directory was created by an elevated
                // admin, the standard user lacks WriteDAC rights. It is better
                // to fail when actually writing the token file later than to
                // crash the entire CLI during startup.
            }

            return;
        }

        directory.Create(security);
    }
}

public sealed record TreaderBootstrapOptions(
    string? ApplicationDataRoot = null,
    string? GameRoot = null,
    string? GameUserDataRoot = null);
