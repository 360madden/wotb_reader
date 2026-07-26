using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

[TestClass]
public sealed class CliOutputTests
{
    [TestMethod]
    public async Task JsonOutputUsesStableEnvelopeShape()
    {
        Guid correlationId = Guid.CreateVersion7();
        CliExecution execution = new(
            CliExitCode.Success,
            new CliEnvelope(
                "1",
                Success: true,
                correlationId,
                new { status = "ok" },
                Warnings: [],
                Errors: []),
            "ok");
        using StringWriter writer = new();

        await CliOutput.WriteAsync(execution, json: true, writer, CancellationToken.None);

        string output = writer.ToString();
        StringAssert.Contains(output, "\"schemaVersion\": \"1\"");
        StringAssert.Contains(output, "\"success\": true");
        StringAssert.Contains(output, correlationId.ToString());
    }
}
