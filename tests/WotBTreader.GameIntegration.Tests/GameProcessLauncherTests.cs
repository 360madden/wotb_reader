using System.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameProcessLauncherTests
{
    [TestMethod]
    public void CreateStartInfo_UsesExecutableDirectoryAndNormalWindow()
    {
        const string executablePath = @"C:\Games\World_of_Tanks_Blitz\wotblitz.exe";
        InstalledGameIdentity identity = new(
            executablePath,
            "11.19.0.10",
            new ContentHash(new string('a', ContentHash.Sha256HexLength)),
            Path.Combine(Path.GetDirectoryName(executablePath)!, "Data"),
            []);

        ProcessStartInfo startInfo = GameProcessLauncher.CreateStartInfo(identity);

        Assert.AreEqual(executablePath, startInfo.FileName);
        Assert.AreEqual(Path.GetDirectoryName(executablePath), startInfo.WorkingDirectory);
        Assert.IsTrue(startInfo.UseShellExecute);
        Assert.AreEqual(ProcessWindowStyle.Normal, startInfo.WindowStyle);
    }
}
