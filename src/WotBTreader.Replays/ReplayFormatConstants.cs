namespace WotBTreader.Replays;

internal static class ReplayFormatConstants
{
    public const string MetadataEntry = "meta.json";
    public const string BattleResultsEntry = "battle_results.dat";
    public const string EventStreamEntry = "data.wotreplay";
    public const uint EventStreamMagic = 0x12345678;

    public const long MaximumStrictArchiveBytes = 20 * 1024 * 1024;
    public const long MaximumMetadataBytes = 1024 * 1024;
    public const long MaximumBattleResultsBytes = 8 * 1024 * 1024;
    public const long MaximumEventStreamBytes = 20 * 1024 * 1024;
    public const long MaximumStrictExpandedBytes = 24 * 1024 * 1024;
    public const int MaximumStrictPacketBytes = 200 * 1024;
    public const int MaximumStrictPacketCount = 200_000;
    public const int MaximumPickleOpcodes = 100_000;
    public const int MaximumPickleStackDepth = 4_096;
    public const int MaximumPickleTextBytes = 1024 * 1024;
    public const int MaximumPickleLongBytes = 128;
    public const int MaximumRosterEntries = 64;

    public static readonly IReadOnlySet<string> RequiredEntries =
        new HashSet<string>(StringComparer.Ordinal)
        {
            MetadataEntry,
            BattleResultsEntry,
            EventStreamEntry,
        };
}
