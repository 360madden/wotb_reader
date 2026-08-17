using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Bootstrap.DependencyInjection;
using WotBTreader.Core;

return await ReplayInspector.RunAsync(args).ConfigureAwait(false);

internal static class ReplayInspector
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;
    private const int UnsupportedExitCode = 3;
    private const int InvalidInputExitCode = 4;
    private const int InternalFailureExitCode = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        bool includeSensitive = arguments.Contains(
            "--include-sensitive",
            StringComparer.Ordinal);
        string[] paths = arguments
            .Where(argument => !string.Equals(
                argument,
                "--include-sensitive",
                StringComparison.Ordinal))
            .ToArray();
        if (paths.Length != 1)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.invalid_arguments",
                    "Usage: WotBTreader.ReplayInspector <replay.wotbreplay> [--include-sensitive]"));
            return InvalidArgumentsExitCode;
        }

        string replayPath;
        try
        {
            replayPath = Path.GetFullPath(paths[0]);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.invalid_path",
                    "The replay path is invalid."));
            return InvalidArgumentsExitCode;
        }

        if (!File.Exists(replayPath))
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.not_found",
                    "The replay file does not exist."));
            return InvalidInputExitCode;
        }

        using ServiceProvider provider = BuildServiceProvider();

        try
        {
            SourceArtifact artifact = await CreateArtifactAsync(replayPath).ConfigureAwait(false);
            ReplayInput input = new(
                artifact,
                cancellationToken => ValueTask.FromResult<Stream>(
                    new FileStream(
                        replayPath,
                        new FileStreamOptions
                        {
                            Access = FileAccess.Read,
                            Mode = FileMode.Open,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            Share = FileShare.Read,
                        })));
            DecoderLimits limits = DecoderLimits.Default;

            IReplayProbe probe = provider.GetRequiredService<IReplayProbe>();
            OperationResult<ReplayProbeResult> probeResult = await probe.ProbeAsync(
                input,
                limits,
                CancellationToken.None).ConfigureAwait(false);
            if (!probeResult.IsSuccess || probeResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: null,
                    probeResult.Warnings,
                    probeResult.Error);
                return InvalidInputExitCode;
            }

            ReplayDecoderRegistry registry = provider.GetRequiredService<ReplayDecoderRegistry>();
            OperationResult<IReplayDecoder> decoderResult = registry.Select(probeResult.Value);
            if (!decoderResult.IsSuccess || decoderResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: new
                    {
                        probeResult.Value.GameVersion,
                        probeResult.Value.FormatVersion,
                    },
                    probeResult.Warnings,
                    decoderResult.Error);
                return UnsupportedExitCode;
            }

            IReplayDecoder decoder = decoderResult.Value;
            ReplayDecodeRequest request = new(
                input,
                DecodeRunId.New(),
                probeResult.Value,
                limits);
            OperationResult<ReplayDecodeProjection> decodeResult = await decoder.DecodeAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!decodeResult.IsSuccess || decodeResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: null,
                    decodeResult.Warnings,
                    decodeResult.Error);
                return InvalidInputExitCode;
            }

            ReplayDecodeProjection projection = decodeResult.Value;
            object data = new
            {
                decoder = new
                {
                    projection.DecodeRun.DecoderId,
                    projection.DecodeRun.DecoderVersion,
                    projection.DecodeRun.SchemaVersion,
                    projection.DecodeRun.Capabilities,
                },
                session = projection.Session is null
                    ? null
                    : new
                    {
                        projection.Session.GameVersion,
                        projection.Session.MapId,
                        projection.Session.MapName,
                        projection.Session.BattleTimeUtc,
                        durationSeconds = projection.Session.Duration?.TotalSeconds,
                        hasViewpoint = projection.Session.ViewpointParticipantId is not null,
                    },
                participants = projection.Participants.Select(
                    (participant, index) => new
                    {
                        ordinal = index + 1,
                        accountId = includeSensitive ? participant.AccountId : null,
                        // Player names are public Wargaming statistics and are
                        // never gated; account IDs and clan tags stay opt-in.
                        playerName = participant.PlayerName,
                        clanTag = includeSensitive ? participant.ClanTag : null,
                        participant.EntityId,
                        participant.TeamNumber,
                        // Tag 103 (player-results info): the compact vehicle
                        // descriptor, identical on both decoders to the Rust
                        // oracle's tank_id.
                        participant.VehicleCompactDescriptor,
                        participant.TankId,
                        participant.TankName,
                        participant.TankClass,
                        participant.BotStatus,
                        // Battle-results stats from battle_results.dat
                        // (root.301.2), cross-checked against the parser schema.
                        battleStats = participant.BattleStats is null
                            ? null
                            : new
                            {
                                participant.BattleStats.CreditsEarned,
                                participant.BattleStats.BaseXp,
                                participant.BattleStats.Shots,
                                participant.BattleStats.HitsDealt,
                                participant.BattleStats.PenetrationsDealt,
                                participant.BattleStats.DamageDealt,
                                participant.BattleStats.DamageAssisted1,
                                participant.BattleStats.DamageAssisted2,
                                participant.BattleStats.HitsReceived,
                                participant.BattleStats.NonPenetratingHitsReceived,
                                participant.BattleStats.PenetrationsReceived,
                                participant.BattleStats.EnemiesDamaged,
                                participant.BattleStats.EnemiesDestroyed,
                                participant.BattleStats.VictoryPointsEarned,
                                participant.BattleStats.VictoryPointsSeized,
                                participant.BattleStats.MmRating,
                                participant.BattleStats.DamageBlocked,
                            },
                    }),

                typedPackets = new
                {
                    // Type-0 BasePlayerCreate header, decoded from the event
                    // stream (third arena-identity source). Null when the
                    // replay has no such packet.
                    basePlayerCreate = FindBasePlayerCreate(projection),
                    // Arena participants derived from updateArena2 (type 8 /
                    // subtype 48) packets: the players whose identity came
                    // from the arena stream rather than battle results alone.
                    updateArenaRoster = projection.Participants
                        .Where(participant => participant.EntityId is not null)
                        .Select(participant => new
                        {
                            entityId = participant.EntityId,
                            accountId = includeSensitive ? participant.AccountId : null,
                            playerName = participant.PlayerName,
                            teamNumber = participant.TeamNumber,
                        })
                        .ToArray(),
                },
                counts = new
                {
                    participants = projection.Participants.Count,
                    positions = projection.Positions.Count,
                    events = projection.Events.Count,
                    rawRecords = projection.RawRecords.Count,
                    viewpointShots = PublishedMarkerShotJoin
                        .ListViewpointShotTimes(projection).Count,
                    distinctViewpointShellSignatures = PublishedMarkerShotJoin
                        .ListViewpointShellSignatures(projection).Count,
                    viewpointShellSignatures = PublishedMarkerShotJoin
                        .ListViewpointShellSignatures(projection)
                        .Select(row => new { hex = row.Hex, count = row.Count }),
                },
                sensitiveFieldsIncluded = includeSensitive,
            };
            WriteEnvelope(
                success: true,
                data,
                decodeResult.Warnings,
                error: null);
            return SuccessExitCode;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.read_failed",
                    "The replay could not be read."));
            return InvalidInputExitCode;
        }
        catch (Exception)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.internal_failure",
                    "The inspector failed without exposing sensitive internals."));
            return InternalFailureExitCode;
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddWotBTreaderReplayTooling();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static async ValueTask<SourceArtifact> CreateArtifactAsync(string path)
    {
        FileInfo info = new(path);
        await using FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read,
            });
        byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return new SourceArtifact(
            SourceArtifactId.New(),
            new ContentHash(Convert.ToHexString(hash).ToLowerInvariant()),
            info.Length,
            "application/vnd.wargaming.wotb-replay",
            ".wotbreplay",
            DateTimeOffset.UtcNow,
            "1");
    }

    private static object? FindBasePlayerCreate(ReplayDecodeProjection projection)
    {
        foreach (RawRecord record in projection.RawRecords)
        {
            if (!string.Equals(
                    record.RecordKind,
                    "event-stream.packet",
                    StringComparison.Ordinal) ||
                string.IsNullOrEmpty(record.PropertiesJson))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(record.PropertiesJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("basePlayerCreate", out JsonElement typed))
            {
                continue;
            }

            return new
            {
                authorNickname = typed.TryGetProperty(
                    "authorNickname",
                    out JsonElement author) ? author.GetString() : null,
                arenaUniqueId = typed.TryGetProperty(
                    "arenaUniqueId",
                    out JsonElement arenaId) ? arenaId.GetUInt64() : (ulong?)null,
                arenaTypeId = typed.TryGetProperty(
                    "arenaTypeId",
                    out JsonElement arenaType) ? arenaType.GetUInt32() : (uint?)null,
            };
        }

        return null;
    }

    private static void WriteEnvelope(
        bool success,
        object? data,
        IReadOnlyList<string> warnings,
        ApplicationError? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1",
                success,
                correlationId = Guid.CreateVersion7(),
                data,
                warnings,
                error,
            },
            JsonOptions));
    }
}
