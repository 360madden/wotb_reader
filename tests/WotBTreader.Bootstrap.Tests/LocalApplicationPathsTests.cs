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
            Assert.Inconclusive("This test exercises a Windows-only code path.");
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

    [TestMethod]
    public void ProtectRendezvousFile_TightensPermissiveInheritedAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        VerifyWindowsFileProtection();
    }

    [TestMethod]
    public void VerifyRendezvousFile_RejectsPermissiveAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        VerifyWindowsFileRejectsPermissiveAcl();
    }

    [TestMethod]
    public void VerifyRendezvousFile_RejectsMissingFile()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-file-missing-{Guid.CreateVersion7():N}");
        string missing = Path.Combine(root, "rendezvous", "web.json");
        Assert.ThrowsExactly<FileNotFoundException>(
            () => LocalApplicationPaths.VerifyRendezvousFile(missing));
    }

    [TestMethod]
    public void VerifyRendezvousFile_RejectsReparsePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        VerifyWindowsFileRejectsReparsePoint();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsFileProtection()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-file-acl-{Guid.CreateVersion7():N}");
        string rendezvous = Path.Combine(root, "rendezvous");
        string file = Path.Combine(rendezvous, "web.json");
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        try
        {
            // The permissive parent grants World read so the assertion proves
            // inheritance was severed on the file, not merely hidden behind a
            // parent that already happened to be private.
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
            File.WriteAllText(file, "{\"schemaVersion\":\"1.0\"}");

            LocalApplicationPaths.ProtectRendezvousFile(file);

            FileSecurity actual = new FileInfo(file).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            Assert.IsTrue(
                actual.AreAccessRulesProtected,
                "Rendezvous file ACL inheritance must be disabled.");
            Assert.AreEqual(
                owner,
                actual.GetOwner(typeof(SecurityIdentifier)),
                "The rendezvous file must be owned by the current user.");

            AuthorizationRuleCollection rules = actual.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier));
            Assert.HasCount(
                1,
                rules,
                "Only the current user's explicit allow rule may remain on the file.");
            Assert.IsInstanceOfType<FileSystemAccessRule>(rules[0]);
            var rule = (FileSystemAccessRule)rules[0]!;
            Assert.AreEqual(owner, rule.IdentityReference);
            Assert.IsFalse(rule.IsInherited);
            Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
            Assert.AreEqual(
                FileSystemRights.FullControl,
                rule.FileSystemRights & FileSystemRights.FullControl);

            // The post-move verification must accept the freshly protected file.
            LocalApplicationPaths.VerifyRendezvousFile(file);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsFileRejectsPermissiveAcl()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-file-verify-{Guid.CreateVersion7():N}");
        string rendezvous = Path.Combine(root, "rendezvous");
        string file = Path.Combine(rendezvous, "web.json");
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        try
        {
            Directory.CreateDirectory(rendezvous);
            File.WriteAllText(file, "{}");
            var permissive = new FileSecurity();
            permissive.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            permissive.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(permissive);

            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => LocalApplicationPaths.VerifyRendezvousFile(file));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsFileRejectsReparsePoint()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-file-reparse-{Guid.CreateVersion7():N}");
        string rendezvous = Path.Combine(root, "rendezvous");
        string target = Path.Combine(root, "target.txt");
        string link = Path.Combine(rendezvous, "web.json");

        try
        {
            Directory.CreateDirectory(rendezvous);
            File.WriteAllText(target, "{}");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or PlatformNotSupportedException)
            {
                Assert.Inconclusive(
                    "Symbolic-link creation needs developer mode or elevation; " +
                    "the reparse branch cannot be exercised without it.");
                return;
            }

            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => LocalApplicationPaths.VerifyRendezvousFile(link));
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
