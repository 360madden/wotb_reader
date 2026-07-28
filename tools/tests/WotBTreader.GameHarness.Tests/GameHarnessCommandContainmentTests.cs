using System.Diagnostics;
using System.Globalization;

namespace WotBTreader.GameHarness.Tests;

[TestClass]
public sealed class GameHarnessCommandContainmentTests
{
    private const string DenialSuffix =
        "is disabled pending the centralized offline-replay verification gate.";

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

    private static void AssertDenied(string command, HarnessInvocation result)
    {
        Assert.AreEqual((int)HarnessExitCode.UnsupportedCapability, result.ExitCode);
        Assert.AreEqual($"{command} {DenialSuffix}", result.StandardError.Trim());
        Assert.IsEmpty(result.StandardOutput);
    }

    private sealed record HarnessInvocation(int ExitCode, string StandardOutput, string StandardError);
}
