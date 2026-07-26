using Serilog;
using Serilog.Core;
using Serilog.Events;
using WotBTreader.Bootstrap.Logging;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class RedactingLogEventEnricherTests
{
    [TestMethod]
    public void SensitiveStructuredPropertiesAreRedacted()
    {
        CollectingSink sink = new();
        using Logger logger = new LoggerConfiguration()
            .Enrich.With<RedactingLogEventEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "Import {CandidatePath} for {PlayerName} with {ArtifactId}",
            @"C:\private\replay.wotbreplay",
            "private-player",
            "safe-artifact-id");

        Assert.HasCount(1, sink.Events);
        LogEvent logEvent = sink.Events[0];
        Assert.AreEqual("\"[REDACTED]\"", logEvent.Properties["CandidatePath"].ToString());
        Assert.AreEqual("\"[REDACTED]\"", logEvent.Properties["PlayerName"].ToString());
        Assert.AreEqual("\"safe-artifact-id\"", logEvent.Properties["ArtifactId"].ToString());
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
