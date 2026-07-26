using Serilog.Core;
using Serilog.Events;

namespace WotBTreader.Bootstrap.Logging;

public sealed class RedactingLogEventEnricher : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    private static readonly string[] SensitiveNameFragments =
    [
        "authorization",
        "candidatepath",
        "chat",
        "credential",
        "executablepath",
        "filepath",
        "password",
        "playername",
        "accountid",
        "screenshot",
        "secret",
        "token",
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (string propertyName in logEvent.Properties.Keys.ToArray())
        {
            if (IsSensitive(propertyName))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(propertyName, Redacted));
            }
        }
    }

    private static bool IsSensitive(string propertyName)
    {
        foreach (string fragment in SensitiveNameFragments)
        {
            if (propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
