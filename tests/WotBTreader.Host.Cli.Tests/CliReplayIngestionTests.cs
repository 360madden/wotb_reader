using System.Text.Json;
using WotBTreader.Host.Cli.Cli;
using WotBTreader.TestSupport;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// Proves the decode path end to end through the CLI: a replay archive is
/// imported, decoded, persisted, and then visible to the query commands. Uses a
/// synthetic fixture so the pass never depends on private game files.
/// </summary>
[TestClass]
public sealed class CliReplayIngestionTests
{
    [TestMethod]
    public async Task ImportDecodesAReplayAndTheQueryCommandsReportIt()
    {
        using TemporaryDataRoot root = new();
        string replayPath = await WriteSyntheticReplayAsync(root);

        CliRun imported = await RunAsync(root, "import", replayPath);

        Assert.AreEqual(0, imported.ExitCode, imported.Diagnostic);
        JsonElement summary = imported.Data.GetProperty("decodeRun");
        Assert.IsFalse(imported.Data.GetProperty("artifactAlreadyExisted").GetBoolean());
        Assert.IsGreaterThan(
            0,
            summary.GetProperty("participantCount").GetInt32(),
            $"The decoder recorded no participants. {imported.Diagnostic}");
        Assert.IsGreaterThan(
            0,
            summary.GetProperty("positionCount").GetInt32(),
            $"The decoder recorded no position samples. {imported.Diagnostic}");

        CliRun sessions = await RunAsync(root, "sessions");

        Assert.AreEqual(0, sessions.ExitCode, sessions.Diagnostic);
        Assert.AreEqual(
            1,
            sessions.Data.GetArrayLength(),
            $"The imported decode run should be the only session. {sessions.Diagnostic}");

        CliRun inspected = await RunAsync(root, "inspect", DecodeRunId(summary));

        Assert.AreEqual(0, inspected.ExitCode, inspected.Diagnostic);
        Assert.AreEqual(
            DecodeRunId(summary),
            DecodeRunId(inspected.Data),
            "inspect must return the decode run that import created.");
    }

    [TestMethod]
    public async Task ReimportingTheSameBytesReusesTheArtifactAndCreatesANewRun()
    {
        using TemporaryDataRoot root = new();
        string replayPath = await WriteSyntheticReplayAsync(root);

        CliRun first = await RunAsync(root, "import", replayPath);
        CliRun second = await RunAsync(root, "import", replayPath);

        Assert.AreEqual(0, first.ExitCode, first.Diagnostic);
        Assert.AreEqual(0, second.ExitCode, second.Diagnostic);

        // The content-addressed store must not duplicate identical bytes, but
        // reprocessing is always a new immutable decode run.
        Assert.IsFalse(first.Data.GetProperty("artifactAlreadyExisted").GetBoolean());
        Assert.IsTrue(
            second.Data.GetProperty("artifactAlreadyExisted").GetBoolean(),
            $"Identical bytes must resolve to the existing artifact. {second.Diagnostic}");
        Assert.AreNotEqual(
            DecodeRunId(first.Data.GetProperty("decodeRun")),
            DecodeRunId(second.Data.GetProperty("decodeRun")),
            "Each import must create a new decode run rather than overwrite one.");
    }

    [TestMethod]
    public async Task ImportRejectsAFileThatIsNotAReplayArchive()
    {
        using TemporaryDataRoot root = new();
        string replayPath = Path.Combine(root.Path, "corrupt.wotbreplay");
        await File.WriteAllBytesAsync(
            replayPath,
            [0x00, 0x01, 0x02, 0x03],
            TestContext.CancellationToken);

        CliRun run = await RunAsync(root, "import", replayPath);

        Assert.AreNotEqual(0, run.ExitCode);
        Assert.IsFalse(run.Root.GetProperty("success").GetBoolean());
        Assert.IsGreaterThan(
            0,
            run.Root.GetProperty("errors").GetArrayLength(),
            $"A rejected import must report a stable error code. {run.Diagnostic}");
    }

    [TestMethod]
    public async Task InspectRejectsAnUnknownDecodeRun()
    {
        using TemporaryDataRoot root = new();

        CliRun run = await RunAsync(root, "inspect", Guid.NewGuid().ToString());

        Assert.AreNotEqual(0, run.ExitCode);
        Assert.IsFalse(run.Root.GetProperty("success").GetBoolean());
    }

    private async Task<string> WriteSyntheticReplayAsync(TemporaryDataRoot root)
    {
        string replayPath = Path.Combine(root.Path, "fixture.wotbreplay");
        await File.WriteAllBytesAsync(
            replayPath,
            SyntheticReplayFactory.CreateReplay(),
            TestContext.CancellationToken);
        return replayPath;
    }

    private async Task<CliRun> RunAsync(TemporaryDataRoot root, params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        string[] full = [.. arguments, "--json", "--data-root", root.Path];

        int exitCode = await CliEntryPoint.RunAsync(
            full,
            output,
            error,
            TestContext.CancellationToken);

        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    // Identifiers are record structs, so the envelope renders them as a nested
    // object rather than a bare string.
    private static string DecodeRunId(JsonElement summary) =>
        summary.GetProperty("decodeRun").GetProperty("id").GetProperty("value").GetString()!;

    public TestContext TestContext { get; set; } = null!;

    private sealed record CliRun(int ExitCode, string StandardOutput, string StandardError)
    {
        public JsonElement Root => JsonDocument.Parse(StandardOutput).RootElement.Clone();

        public JsonElement Data => Root.GetProperty("data");

        public string Diagnostic => $"stdout: {StandardOutput}{Environment.NewLine}stderr: {StandardError}";
    }
}
