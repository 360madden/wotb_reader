using System.Text.Json;
using WotBTreader.Host.Cli.Cli;
using WotBTreader.TestSupport;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// Exercises the read-only <c>probe</c> command: replay game-version report
/// plus the installed-game family compatibility verdict used by the launcher
/// pre-flight guard (Error 126 = client-version mismatch, 2026-08-12).
/// </summary>
[TestClass]
public sealed class CliProbeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProbeReportsSyntheticReplayVersion()
    {
        using TemporaryDataRoot root = new();
        string replayPath = Path.Combine(root.Path, "fixture.wotbreplay");
        await File.WriteAllBytesAsync(
            replayPath,
            SyntheticReplayFactory.CreateReplay(),
            TestContext.CancellationToken);

        StringWriter output = new();
        StringWriter error = new();
        int exitCode = await CliEntryPoint.RunAsync(
            ["probe", replayPath, "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.Success, exitCode, error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement rootElement = document.RootElement;
        Assert.IsTrue(rootElement.GetProperty("success").GetBoolean());
        Assert.IsTrue(rootElement.GetProperty("data").GetProperty("isReplay").GetBoolean());
        Assert.AreEqual(
            "11.18.0",
            rootElement.GetProperty("data").GetProperty("gameVersion").GetString());
    }

    [TestMethod]
    public async Task ProbeRejectsNonWotbreplayExtension()
    {
        using TemporaryDataRoot root = new();
        string notReplay = Path.Combine(root.Path, "fixture.txt");
        await File.WriteAllTextAsync(notReplay, "not a replay", TestContext.CancellationToken);

        StringWriter output = new();
        StringWriter error = new();
        int exitCode = await CliEntryPoint.RunAsync(
            ["probe", notReplay, "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreNotEqual((int)CliExitCode.Success, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task ProbeRequiresExactlyOnePath()
    {
        using TemporaryDataRoot root = new();

        StringWriter output = new();
        StringWriter error = new();
        int exitCode = await CliEntryPoint.RunAsync(
            ["probe", "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreNotEqual((int)CliExitCode.Success, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task ProbeReportsMissingFileAsInvalidInput()
    {
        using TemporaryDataRoot root = new();
        string missing = Path.Combine(root.Path, "missing.wotbreplay");

        StringWriter output = new();
        StringWriter error = new();
        int exitCode = await CliEntryPoint.RunAsync(
            ["probe", missing, "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreNotEqual((int)CliExitCode.Success, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
    }
}
