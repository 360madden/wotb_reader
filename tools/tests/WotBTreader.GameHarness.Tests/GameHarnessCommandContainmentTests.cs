using System.Diagnostics;
using System.Globalization;

namespace WotBTreader.GameHarness.Tests;

[TestClass]
public sealed class GameHarnessCommandContainmentTests
{
    private const string NoHostSuffix =
        ": no web host found. Start the host with 'serve' first, then launch " +
        "a replay via the dashboard or POST /api/v1/game/launch.";

    [TestMethod]
    public async Task ScanIsDeniedBeforeItCanAttachToTheRequestedProcess()
    {
        HarnessInvocation result = await InvokeAsync(
            "scan",
            "int32",
            "1500",
            "--pid",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        AssertDenied("scan", result);
    }

    [TestMethod]
    public async Task ProbeIsDeniedBeforeItCanEnumerateOrAttachToAGameProcess()
    {
        HarnessInvocation result = await InvokeAsync("probe");

        AssertDenied("probe", result);
    }

    [TestMethod]
    public async Task MalformedCapabilityRecordIsDeniedBeforeAnyUnsafeRequest()
    {
        HarnessInvocation result = await InvokeWithRendezvousAsync(
            "{\"schemaVersion\":\"1.0\",\"baseUri\":\"http://127.0.0.1:9182\",\"expiresAtUtc\":\"not-a-time\",\"processId\":1}",
            "discover-pattern",
            "signature",
            "488B90");

        Assert.AreEqual((int)HarnessExitCode.UnsupportedCapability, result.ExitCode);
        Assert.AreEqual("discover-pattern: no web host found.", result.StandardError.Trim());
        Assert.IsEmpty(result.StandardOutput);
    }

    private static async Task<HarnessInvocation> InvokeAsync(params string[] arguments)
    {
        string harnessAssemblyPath = typeof(HarnessExitCode).Assembly.Location;
        string harnessExecutablePath = Path.ChangeExtension(harnessAssemblyPath, ".exe");
        Assert.IsTrue(File.Exists(harnessExecutablePath), $"Harness executable was not found: {harnessExecutablePath}");

        var startInfo = new ProcessStartInfo(harnessExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Point the harness at a rendezvous path that cannot exist, so these
        // black-box tests are hermetic: a stray local host with a valid
        // rendezvous record must never change the expected exit code.
        string hermeticRendezvous = Path.Combine(
            Path.GetTempPath(),
            "wotbtreader-tests",
            Guid.NewGuid().ToString("N"),
            "web.json");
        startInfo.Environment["WOTB_TREADER_RENDEZVOUS_PATH"] = hermeticRendezvous;

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "Harness process did not start.");

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);

        return new HarnessInvocation(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static async Task<HarnessInvocation> InvokeWithRendezvousAsync(
        string rendezvousContents,
        params string[] arguments)
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "wotbtreader-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string rendezvousPath = Path.Combine(tempDirectory, "web.json");
        await File.WriteAllTextAsync(rendezvousPath, rendezvousContents);

        try
        {
            string harnessAssemblyPath = typeof(HarnessExitCode).Assembly.Location;
            string harnessExecutablePath = Path.ChangeExtension(harnessAssemblyPath, ".exe");
            var startInfo = new ProcessStartInfo(harnessExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.Environment["WOTB_TREADER_RENDEZVOUS_PATH"] = rendezvousPath;
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            Assert.IsTrue(process.Start(), "Harness process did not start.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return new HarnessInvocation(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void AssertDenied(string command, HarnessInvocation result)
    {
        Assert.AreEqual((int)HarnessExitCode.UnsupportedCapability, result.ExitCode);
        Assert.AreEqual($"{command}{NoHostSuffix}", result.StandardError.Trim());
        Assert.IsEmpty(result.StandardOutput);
    }

    private sealed record HarnessInvocation(int ExitCode, string StandardOutput, string StandardError);
}
