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
    /// The capability must not be published when the directory cannot be
    /// positively verified as owner-only. Permission failures therefore
    /// propagate to the publisher instead of falling back to inherited access.
    /// </remarks>
    public void EnsureRendezvousDirectory() => EnsureOwnerOnlyDirectory(Rendezvous);

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsOwnerOnlyDirectory(path);
            return;
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, ownerOnly);
        File.SetUnixFileMode(path, ownerOnly);

        UnixFileMode actual = File.GetUnixFileMode(path);
        if (actual != ownerOnly)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory could not be verified as owner-only.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwnerOnlyDirectory(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The rendezvous directory must not be a reparse point.");
            }

            directory.SetAccessControl(security);
        }
        else
        {
            directory.Create(security);
        }

        VerifyWindowsOwnerOnlyDirectory(directory, owner);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyDirectory(
        DirectoryInfo directory,
        SecurityIdentifier expectedOwner)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory identity could not be verified.");
        }

        DirectorySecurity actual = directory.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!actual.AreAccessRulesProtected ||
            !expectedOwner.Equals(actual.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory owner or inheritance boundary is unsafe.");
        }

        AuthorizationRuleCollection rules = actual.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        if (rules.Count != 1 ||
            rules[0] is not FileSystemAccessRule rule ||
            rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow ||
            !expectedOwner.Equals(rule.IdentityReference) ||
            (rule.FileSystemRights & FileSystemRights.FullControl) !=
                FileSystemRights.FullControl ||
            rule.InheritanceFlags !=
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory access rules are not owner-only.");
        }
    }
}

public sealed record TreaderBootstrapOptions(
    string? ApplicationDataRoot = null,
    string? GameRoot = null,
    string? GameUserDataRoot = null,
    TimeSpan? OfflineReplayEvidenceLifetime = null,
    TimeSpan? LifecycleEvidenceTimeout = null);
