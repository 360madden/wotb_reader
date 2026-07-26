using System.Security.AccessControl;
using System.Security.Principal;
using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class LocalApplicationPathsTests
{
    [TestMethod]
    public void RendezvousDirectoryIsReadableOnlyByTheCurrentUser()
    {
        // The rendezvous record carries a live mutation capability, so a custom
        // data root must never let it inherit a permissive parent ACL.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DACL semantics are asserted only on Windows.");
            return;
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-acl-{Guid.CreateVersion7():N}");
        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);
        try
        {
            paths.EnsureDirectoriesExist();

            DirectorySecurity security = new DirectoryInfo(paths.Rendezvous).GetAccessControl();
            Assert.IsTrue(
                security.AreAccessRulesProtected,
                "Inheritance must be severed so a permissive parent cannot re-grant access.");

            FileSystemAccessRule[] rules = [.. security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()];
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;

            Assert.HasCount(1, rules);
            Assert.AreEqual(owner, rules[0].IdentityReference);
            Assert.AreEqual(AccessControlType.Allow, rules[0].AccessControlType);
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
    public void OverrideKeepsAllRuntimeDataUnderChosenRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wotbtreader-path-test"));

        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);

        Assert.AreEqual(root, paths.Root);
        Assert.IsTrue(paths.ContentStore.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Database.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Logs.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Diagnostics.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Rendezvous.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
