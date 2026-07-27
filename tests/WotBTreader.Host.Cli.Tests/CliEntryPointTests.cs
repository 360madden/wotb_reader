using System.Text.Json;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// Exercises the full CLI path in-process: argument parsing, host composition,
/// storage migration at startup, command dispatch, and envelope rendering.
/// </summary>
[TestClass]
public sealed class CliEntryPointTests
{
    [TestMethod]
    public async Task SessionsReturnsAnEmptyEnvelopeOnAFreshDataRoot()
    {
        using TemporaryDataRoot root = new();
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            ["sessions", "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.Success, exitCode, error.ToString());

        // Parsing proves no log line leaked onto stdout ahead of the envelope.
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement rootElement = document.RootElement;
        Assert.IsTrue(rootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("1", rootElement.GetProperty("schemaVersion").GetString());
        Assert.IsEmpty(rootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.AreNotEqual(Guid.Empty, rootElement.GetProperty("correlationId").GetGuid());
    }

    [TestMethod]
    public async Task StartupCreatesStorageBeneathTheRequestedDataRoot()
    {
        using TemporaryDataRoot root = new();
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            ["sessions", "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.Success, exitCode, error.ToString());
        Assert.IsTrue(
            Directory.Exists(System.IO.Path.Combine(root.Path, "content")),
            "The content store must be created beneath the requested data root.");
        Assert.IsNotEmpty(
            Directory.GetFiles(root.Path, "*.db", SearchOption.AllDirectories),
            "Storage migrations must run before the command executes.");
    }

    [TestMethod]
    public async Task UnknownCommandFailsWithArgumentExitCodeAndStderrText()
    {
        using TemporaryDataRoot root = new();
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            ["definitely-not-a-command", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.InvalidArguments, exitCode);
        Assert.IsEmpty(output.ToString());
        Assert.Contains("cli.command.unknown", error.ToString());
    }

    [TestMethod]
    public async Task MissingCommandIsRejectedBeforeAnyHostIsBuilt()
    {
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            [],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.InvalidArguments, exitCode);
        Assert.Contains("cli.command.required", error.ToString());
    }

    [TestMethod]
    public async Task ReservedCommandReportsUnsupportedCapability()
    {
        using TemporaryDataRoot root = new();
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            ["export", "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.UnsupportedCapability, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
    }

    [TestMethod]
    public async Task CompareInspectWithUnknownIdReturnsNotFound()
    {
        using TemporaryDataRoot root = new();
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await CliEntryPoint.RunAsync(
            ["compare", "inspect", Guid.NewGuid().ToString("D"), "--json", "--data-root", root.Path],
            output,
            error,
            TestContext.CancellationToken);

        Assert.AreEqual((int)CliExitCode.InvalidInput, exitCode, error.ToString());
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
    }

    public TestContext TestContext { get; set; } = null!;
}
