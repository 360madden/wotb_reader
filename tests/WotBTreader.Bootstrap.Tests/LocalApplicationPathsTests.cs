using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class LocalApplicationPathsTests
{
    [TestMethod]
    public void RendezvousIsAlwaysUnderLocalAppData()
    {
        // The rendezvous is ephemeral coordination data that must live in a
        // per-user location to avoid ACL hazards with shared/removable roots.
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-{Guid.CreateVersion7():N}");
        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.IsTrue(
            paths.Rendezvous.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
            "Rendezvous must always live under %LocalAppData% regardless of the chosen data root.");
    }

    [TestMethod]
    public void OverrideKeepsAllRuntimeDataUnderChosenRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wotbtreader-path-test"));

        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);

        Assert.AreEqual(root, paths.Root);
        Assert.IsTrue(paths.ContentStore.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Database.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Logs.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Diagnostics.StartsWith(root, StringComparison.OrdinalIgnoreCase));

        // Rendezvous is intentionally outside the custom root — see
        // RendezvousIsAlwaysUnderLocalAppData for rationale.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.IsTrue(
            paths.Rendezvous.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EnsureRendezvousDirectory_ResecuresPermissiveInheritedAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VerifyWindowsRendezvousResecuring();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsRendezvousResecuring()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-acl-{Guid.CreateVersion7():N}");
        string rendezvous = Path.Combine(root, "rendezvous");
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        try
        {
            var permissiveParent = new DirectorySecurity();
            permissiveParent.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            permissiveParent.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            permissiveParent.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            new DirectoryInfo(root).Create(permissiveParent);
            Directory.CreateDirectory(rendezvous);

            LocalApplicationPaths paths = CreatePaths(root, rendezvous);
            paths.EnsureRendezvousDirectory();

            DirectorySecurity actual = new DirectoryInfo(rendezvous)
                .GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
            Assert.IsTrue(
                actual.AreAccessRulesProtected,
                "Rendezvous ACL inheritance must be disabled.");
            Assert.AreEqual(
                owner,
                actual.GetOwner(typeof(SecurityIdentifier)),
                "The rendezvous directory must be owned by the current user.");

            AuthorizationRuleCollection rules = actual.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier));
            Assert.HasCount(
                1,
                rules,
                "Only the current user's explicit allow rule may remain.");
            AuthorizationRule? candidate = rules[0];
            Assert.IsNotNull(candidate);
            Assert.IsInstanceOfType<FileSystemAccessRule>(candidate);
            var rule = (FileSystemAccessRule)candidate;
            Assert.AreEqual(owner, rule.IdentityReference);
            Assert.IsFalse(rule.IsInherited);
            Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
            Assert.AreEqual(
                FileSystemRights.FullControl,
                rule.FileSystemRights & FileSystemRights.FullControl);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static LocalApplicationPaths CreatePaths(
        string root,
        string rendezvous) =>
        new(
            Root: root,
            ContentStore: Path.Combine(root, "content"),
            Database: Path.Combine(root, "treader.db"),
            Logs: Path.Combine(root, "logs"),
            Diagnostics: Path.Combine(root, "diagnostics"),
            Rendezvous: rendezvous);
}
