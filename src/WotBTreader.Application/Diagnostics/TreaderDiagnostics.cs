using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace WotBTreader.Application.Diagnostics;

public static class TreaderDiagnostics
{
    public const string InstrumentationName = "WotBTreader";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName, "1.0.0-alpha");

    public static readonly Meter Meter = new(InstrumentationName, "1.0.0-alpha");

    public static readonly Counter<long> ImportFailures =
        Meter.CreateCounter<long>("wotbtreader.import.failures");

    public static readonly Histogram<double> DecodeDurationMilliseconds =
        Meter.CreateHistogram<double>("wotbtreader.decode.duration", "ms");

    public static readonly Counter<long> UnknownRecords =
        Meter.CreateCounter<long>("wotbtreader.decode.unknown_records");

    public static readonly Counter<long> PacketResynchronizations =
        Meter.CreateCounter<long>("wotbtreader.decode.resynchronizations");

    public static readonly Counter<long> DroppedStreamEvents =
        Meter.CreateCounter<long>("wotbtreader.stream.dropped_events");

    public static readonly Counter<long> CoalescedStreamEvents =
        Meter.CreateCounter<long>("wotbtreader.stream.coalesced_events");

    public static readonly Counter<long> StreamReconnects =
        Meter.CreateCounter<long>("wotbtreader.stream.reconnects");

    public static readonly Counter<long> StaleReplayClocks =
        Meter.CreateCounter<long>("wotbtreader.clock.stale");

    public static readonly Counter<long> MigrationFailures =
        Meter.CreateCounter<long>("wotbtreader.storage.migration_failures");
}

public static class TreaderLogEvents
{
    public static readonly EventId Startup = new(1000, nameof(Startup));
    public static readonly EventId FatalStartup = new(1001, nameof(FatalStartup));
    public static readonly EventId ImportStarted = new(2000, nameof(ImportStarted));
    public static readonly EventId ImportCompleted = new(2001, nameof(ImportCompleted));
    public static readonly EventId ImportFailed = new(2002, nameof(ImportFailed));
    public static readonly EventId DecodeStarted = new(3000, nameof(DecodeStarted));
    public static readonly EventId DecodeCompleted = new(3001, nameof(DecodeCompleted));
    public static readonly EventId DecodeFailed = new(3002, nameof(DecodeFailed));
    public static readonly EventId UnknownRecord = new(3003, nameof(UnknownRecord));
    public static readonly EventId StorageMigration = new(4000, nameof(StorageMigration));
    public static readonly EventId StorageMigrationFailed = new(4001, nameof(StorageMigrationFailed));
    public static readonly EventId ComparisonCompleted = new(5000, nameof(ComparisonCompleted));
    public static readonly EventId StreamGap = new(6000, nameof(StreamGap));
    public static readonly EventId ClockStale = new(7000, nameof(ClockStale));
    public static readonly EventId HarnessDenied = new(8000, nameof(HarnessDenied));
    public static readonly EventId HarnessAction = new(8001, nameof(HarnessAction));
    public static readonly EventId BackgroundFailure = new(9000, nameof(BackgroundFailure));
}
