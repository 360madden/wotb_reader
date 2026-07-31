using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Replays;

/// <summary>
/// Evidence-first decoder for the WotB 11.18 replay format.
/// </summary>
/// <remarks>
/// This is an independent implementation informed by the MIT-licensed
/// <see href="https://github.com/eigenein/wotbreplay-parser">
/// eigenein/wotbreplay-parser</see> project and
/// <see href="https://github.com/A158Coke/WotbTools/blob/main/docs/replay-data.md">
/// A158Coke/WotbTools replay-data</see> documentation. Those projects are
/// format references and test oracles; their implementations are not embedded
/// here.
/// </remarks>
public sealed class WotbReplayDecoder : IReplayDecoder
{
    public const string DecoderId = "wotb-11.x-strict";
    private const string DecoderVersion = "0.1.0";
    private const string SchemaVersion = "1";

    private readonly IInstalledGameMetadataProvider[] _metadataProviders;

    public WotbReplayDecoder(IEnumerable<IInstalledGameMetadataProvider>? metadataProviders = null)
    {
        _metadataProviders = metadataProviders?.ToArray() ?? [];
    }

    public DecoderDescriptor Descriptor { get; } = new(
        DecoderId,
        DecoderVersion,
        SchemaVersion,
        new HashSet<string>(StringComparer.Ordinal)
        {
            "11.18.0",
            "11.18.0.7",
            "11.19.0",
            "11.19.0.10",
        });

    public bool CanDecode(ReplayProbeResult probe) =>
        probe.IsReplay &&
        probe.GameVersion is not null &&
        IsSupportedVersion(probe.GameVersion);

    public async ValueTask<OperationResult<ReplayDecodeProjection>> DecodeAsync(
        ReplayDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        using Activity? activity = TreaderDiagnostics.ActivitySource.StartActivity("replay.decode");
        activity?.SetTag("replay.decoder", DecoderId);
        activity?.SetTag("replay.decoder.version", DecoderVersion);

        if (!CanDecode(request.Probe))
        {
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(
                    "replay.unsupported_version",
                    "No strict decoder is available for this replay game version."));
        }

        if (request.Limits.MaximumDecodeDuration <= TimeSpan.Zero)
        {
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(
                    "replay.invalid_limits",
                    "The decode-duration limit must be positive."));
        }

        using CancellationTokenSource timeout = new(request.Limits.MaximumDecodeDuration);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        CancellationToken decodeToken = linked.Token;
        try
        {
            ValidatedReplayArchive archive = await ReplayArchiveReader
                .ReadAsync(request.Input, request.Limits, decodeToken)
                .ConfigureAwait(false);
            WotbReplayMetadata metadata = WotbReplayMetadata.Parse(
                archive[ReplayFormatConstants.MetadataEntry],
                request.Limits);
            if (!IsSupportedVersion(metadata.Version))
            {
                return OperationResult.Failure<ReplayDecodeProjection>(
                    new ApplicationError(
                        "replay.unsupported_version",
                        "The strict decoder supports only WotB 11.18/11.19 replay evidence."));
            }

            BattleResultsData battleResults = BattleResultsReader.Read(
                archive[ReplayFormatConstants.BattleResultsEntry],
                request.Limits);
            EventStreamScan eventStream = EventStreamReader.Scan(
                archive[ReplayFormatConstants.EventStreamEntry],
                request.Limits,
                metadata.Duration,
                decodeToken);
            if (!EventStreamReader.IsCompatibleStreamVersion(eventStream.Header.ClientVersion))
            {
                return OperationResult.Failure<ReplayDecodeProjection>(
                    new ApplicationError(
                        "replay.unsupported_stream_version",
                        "The event stream is not compatible with WotB 11.18."));
            }

            List<string> warnings = [.. request.Probe.Warnings, .. eventStream.Warnings];
            if (!string.Equals(
                    NormalizeVersion(metadata.Version),
                    NormalizeVersion(eventStream.Header.ClientVersion),
                    StringComparison.Ordinal))
            {
                warnings.Add("Metadata and event-stream client versions do not match.");
            }

            string tupleArenaIdentity = battleResults.ArenaIdentity.ToString(
                CultureInfo.InvariantCulture);
            if (metadata.ArenaIdentity is not null &&
                !string.Equals(
                    metadata.ArenaIdentity,
                    tupleArenaIdentity,
                    StringComparison.Ordinal))
            {
                warnings.Add("Metadata and battle-results arena identities do not match.");
            }

            List<RawRecord> rawRecords = [];
            long rawOrdinal = 0;
            AddRaw(
                rawRecords,
                request,
                ref rawOrdinal,
                "archive.metadata",
                replayTime: null,
                new BinaryEvidence(
                    ReplayFormatConstants.MetadataEntry,
                    0,
                    archive[ReplayFormatConstants.MetadataEntry].Length,
                    archive[ReplayFormatConstants.MetadataEntry]),
                new { schema = "meta.json" });
            AddRaw(
                rawRecords,
                request,
                ref rawOrdinal,
                "archive.battle-results",
                replayTime: null,
                battleResults.WholeEntryEvidence,
                new { schema = "pickle2+protobuf" });
            AddRaw(
                rawRecords,
                request,
                ref rawOrdinal,
                "event-stream.header",
                replayTime: null,
                new BinaryEvidence(
                    ReplayFormatConstants.EventStreamEntry,
                    0,
                    eventStream.Header.EncodedLength,
                    archive[ReplayFormatConstants.EventStreamEntry]
                        .AsMemory(0, eventStream.Header.EncodedLength)),
                new
                {
                    clientVersion = NormalizeVersion(eventStream.Header.ClientVersion),
                });

            foreach (UnknownProtobufEvidence unknownField in battleResults.UnknownFields)
            {
                AddRaw(
                    rawRecords,
                    request,
                    ref rawOrdinal,
                    "protobuf.unknown-field",
                    replayTime: null,
                    unknownField.Evidence,
                    new
                    {
                        path = unknownField.Path,
                        fieldNumber = unknownField.FieldNumber,
                        wireType = (int)unknownField.WireType,
                    });
            }

            foreach (EventStreamGap gap in eventStream.Gaps)
            {
                AddRaw(
                    rawRecords,
                    request,
                    ref rawOrdinal,
                    "event-stream.gap",
                    replayTime: null,
                    new BinaryEvidence(
                        ReplayFormatConstants.EventStreamEntry,
                        gap.Offset,
                        gap.Length,
                        gap.Bytes),
                    new { gap.Reason });
            }

            Dictionary<long, ArenaParticipantObservation> arenaByEntity = [];
            List<PositionObservation> positions = [];
            List<DamageObservation> damageEvents = [];
            List<EventPacket> battleEndPackets = [];
            foreach (EventPacket packet in eventStream.Packets)
            {
                decodeToken.ThrowIfCancellationRequested();
                bool decoded = false;
                if (EventPacketDecoders.TryReadArenaParticipants(
                        packet,
                        request.Limits,
                        out IReadOnlyList<ArenaParticipantObservation> arenaParticipants,
                        out string? arenaWarning))
                {
                    decoded = true;
                    MergeArenaParticipants(arenaByEntity, arenaParticipants, warnings);
                }
                else if (arenaWarning is not null)
                {
                    warnings.Add(arenaWarning);
                }

                string? positionWarning = null;
                if (!decoded &&
                    EventPacketDecoders.TryReadPosition(
                        packet,
                        out PositionObservation? position,
                        out positionWarning))
                {
                    decoded = true;
                    positions.Add(position!);
                }
                else if (positionWarning is not null)
                {
                    warnings.Add(positionWarning);
                }

                string? damageWarning = null;
                if (!decoded &&
                    EventPacketDecoders.TryReadDirectDamage(
                        packet,
                        out DamageObservation? damage,
                        out damageWarning))
                {
                    decoded = true;
                    damageEvents.Add(damage!);
                }
                else if (damageWarning is not null)
                {
                    warnings.Add(damageWarning);
                }

                if (!decoded && packet.Type == 14)
                {
                    decoded = true;
                    battleEndPackets.Add(packet);
                }

                if (!decoded)
                {
                    uint? subtype = EventPacketDecoders.ReadEntityMethodSubtype(packet);
                    AddRaw(
                        rawRecords,
                        request,
                        ref rawOrdinal,
                        "event-stream.packet",
                        TimeSpan.FromSeconds(packet.ClockSeconds),
                        EventPacketDecoders.EvidenceForPacket(packet),
                        new
                        {
                            packetType = packet.Type,
                            entityMethodSubtype = subtype,
                        });
                }
            }

            EnrichmentResult enrichment = await EnrichAsync(
                battleResults.Participants.Values
                    .Select(participant => participant.TankCompactDescriptor)
                    .Concat(arenaByEntity.Values.Select(participant => participant.TankCompactDescriptor))
                    .Where(descriptor => descriptor is not null)
                    .Select(descriptor => descriptor!.Value),
                metadata,
                decodeToken).ConfigureAwait(false);
            warnings.AddRange(enrichment.Warnings);

            BattleSessionId sessionId = BattleSessionId.New();
            ParticipantProjection participantProjection = BuildParticipants(
                sessionId,
                request,
                battleResults.Participants,
                arenaByEntity,
                metadata,
                enrichment.Vehicles,
                warnings);
            BattleSession session = new(
                sessionId,
                request.DecodeRunId,
                metadata.Version,
                tupleArenaIdentity,
                metadata.MapId?.ToString(CultureInfo.InvariantCulture),
                enrichment.MapName ?? metadata.MapName,
                metadata.BattleTimeUtc ?? battleResults.BattleTimeUtc,
                metadata.Duration,
                participantProjection.ViewpointParticipantId,
                SchemaVersion);

            List<PositionSample> positionSamples = BuildPositions(
                sessionId,
                participantProjection.ParticipantByEntity,
                request,
                positions);
            List<CanonicalEvent> canonicalEvents = BuildEvents(
                sessionId,
                participantProjection,
                request,
                positionSamples,
                damageEvents,
                battleEndPackets);

            ReplayCapability capabilities =
                ReplayCapability.Metadata |
                ReplayCapability.BattleResults |
                ReplayCapability.UnknownRecordsPreserved;
            if (participantProjection.Participants.Count > 0)
            {
                capabilities |= ReplayCapability.Participants;
            }

            if (participantProjection.Participants.Any(participant => participant.TeamNumber is not null))
            {
                capabilities |= ReplayCapability.Teams;
            }

            if (participantProjection.Participants.Any(participant => participant.EntityId is not null))
            {
                capabilities |= ReplayCapability.EntityMapping;
            }

            if (positionSamples.Count > 0)
            {
                capabilities |= ReplayCapability.Positions;
            }

            if (damageEvents.Count > 0)
            {
                capabilities |= ReplayCapability.Damage;
            }

            if (battleEndPackets.Count > 0)
            {
                capabilities |= ReplayCapability.Lifecycle;
            }

            if (enrichment.UsedInstalledMetadata)
            {
                capabilities |= ReplayCapability.InstalledGameMetadata;
            }

            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            DecodeRun decodeRun = new(
                request.DecodeRunId,
                request.Input.Artifact.Id,
                DecoderId,
                DecoderVersion,
                SchemaVersion,
                DecodeRunStatus.Succeeded,
                capabilities,
                completedAt - stopwatch.Elapsed,
                completedAt,
                FailureCode: null,
                FailureSummary: null);
            ReplayDecodeProjection projection = new(
                decodeRun,
                session,
                participantProjection.Participants,
                positionSamples,
                canonicalEvents,
                rawRecords,
                warnings);
            activity?.SetTag("replay.participant.count", projection.Participants.Count);
            activity?.SetTag("replay.position.count", projection.Positions.Count);
            activity?.SetTag("replay.raw_record.count", projection.RawRecords.Count);
            return OperationResult.Success(projection, warnings.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "replay.cancelled");
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(
                    "replay.cancelled",
                    "Replay decoding was cancelled.",
                    Retryable: true));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "replay.decode_timeout");
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(
                    "replay.decode_timeout",
                    "Replay decoding exceeded its time limit.",
                    Retryable: true));
        }
        catch (ReplayFormatException exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Code);
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(exception.Code, exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException or
            ArgumentException or
            JsonException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "replay.decode_failed");
            return OperationResult.Failure<ReplayDecodeProjection>(
                new ApplicationError(
                    "replay.decode_failed",
                    "The replay could not be decoded safely."));
        }
        finally
        {
            stopwatch.Stop();
            TreaderDiagnostics.DecodeDurationMilliseconds.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    internal static bool IsSupportedVersion(string version)
    {
        string normalized = NormalizeVersion(version);
        return string.Equals(normalized, "11.18.0", StringComparison.Ordinal) ||
               string.Equals(normalized, "11.19.0", StringComparison.Ordinal);
    }

    internal static string NormalizeVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int suffix = version.IndexOf('_', StringComparison.Ordinal);
        string withoutPlatform = suffix < 0 ? version : version[..suffix];
        string[] components = withoutPlatform.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return components.Length >= 3
            ? string.Join('.', components.Take(3))
            : withoutPlatform;
    }

    private static void MergeArenaParticipants(
        Dictionary<long, ArenaParticipantObservation> destination,
        IReadOnlyList<ArenaParticipantObservation> observations,
        List<string> warnings)
    {
        foreach (ArenaParticipantObservation observation in observations)
        {
            if (destination.TryGetValue(
                    observation.EntityId,
                    out ArenaParticipantObservation? existing))
            {
                if ((existing.AccountId is not null &&
                     observation.AccountId is not null &&
                     existing.AccountId != observation.AccountId) ||
                    (existing.TeamNumber is not null &&
                     observation.TeamNumber is not null &&
                     existing.TeamNumber != observation.TeamNumber))
                {
                    warnings.Add(
                        "Conflicting updateArena2 participant mappings were retained; the first mapping remains canonical.");
                }

                continue;
            }

            destination.Add(observation.EntityId, observation);
        }
    }

    private async ValueTask<EnrichmentResult> EnrichAsync(
        IEnumerable<int> tankDescriptors,
        WotbReplayMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (_metadataProviders.Length == 0)
        {
            return EnrichmentResult.Empty;
        }

        List<string> warnings = [];
        foreach (IInstalledGameMetadataProvider provider in _metadataProviders)
        {
            OperationResult<GameMetadataContext> contextResult =
                await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!contextResult.IsSuccess || contextResult.Value is null)
            {
                warnings.Add("An installed-game metadata provider was unavailable.");
                continue;
            }

            Dictionary<int, VehicleMetadata> vehicles = [];
            foreach (int descriptor in tankDescriptors.Distinct())
            {
                OperationResult<VehicleMetadata> vehicleResult =
                    await provider.ResolveVehicleAsync(
                        contextResult.Value,
                        descriptor,
                        cancellationToken).ConfigureAwait(false);
                if (vehicleResult.IsSuccess && vehicleResult.Value is not null)
                {
                    vehicles[descriptor] = vehicleResult.Value;
                }
            }

            string? mapName = null;
            if (metadata.MapName is not null)
            {
                OperationResult<MapMetadata> mapResult = await provider.ResolveMapAsync(
                    contextResult.Value,
                    metadata.MapName,
                    cancellationToken).ConfigureAwait(false);
                if (mapResult.IsSuccess && mapResult.Value is not null)
                {
                    mapName = mapResult.Value.DisplayName;
                }
            }

            return new EnrichmentResult(
                vehicles,
                mapName,
                vehicles.Count > 0 || mapName is not null,
                warnings);
        }

        return new EnrichmentResult(
            new Dictionary<int, VehicleMetadata>(),
            null,
            false,
            warnings);
    }

    private static ParticipantProjection BuildParticipants(
        BattleSessionId sessionId,
        ReplayDecodeRequest request,
        IReadOnlyDictionary<long, BattleParticipantObservation> battleParticipants,
        IReadOnlyDictionary<long, ArenaParticipantObservation> arenaByEntity,
        WotbReplayMetadata metadata,
        IReadOnlyDictionary<int, VehicleMetadata> vehicleMetadata,
        List<string> warnings)
    {
        Dictionary<long, ArenaParticipantObservation> arenaByAccount = arenaByEntity.Values
            .Where(observation => observation.AccountId is not null)
            .GroupBy(observation => observation.AccountId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        ArenaParticipantObservation[] descriptorComparisons = arenaByEntity.Values
            .Where(observation =>
                observation.AccountId is not null &&
                observation.TankCompactDescriptor is not null &&
                battleParticipants.TryGetValue(
                    observation.AccountId.Value,
                    out BattleParticipantObservation? battle) &&
                battle.TankCompactDescriptor is not null)
            .ToArray();
        bool arenaTankDescriptorLayoutValidated =
            descriptorComparisons.Length > 0 &&
            descriptorComparisons.All(observation =>
                battleParticipants[observation.AccountId!.Value].TankCompactDescriptor ==
                observation.TankCompactDescriptor);
        if (!arenaTankDescriptorLayoutValidated)
        {
            warnings.Add(
                "The updateArena2 stats descriptor layout could not be cross-validated; unmatched tank descriptors remain unknown.");
        }

        int unmatchedEntityCount = arenaByEntity.Values.Count(
            observation => observation.AccountId is null ||
                           !battleParticipants.ContainsKey(observation.AccountId.Value));
        int mergedCount = battleParticipants.Count + unmatchedEntityCount;
        if (mergedCount > ReplayFormatConstants.MaximumRosterEntries)
        {
            throw new ReplayFormatException(
                "replay.participant_count_limit",
                "The merged participant roster exceeds the participant limit.");
        }

        List<Participant> participants = [];
        Dictionary<long, ParticipantId> participantByAccount = [];
        Dictionary<long, ParticipantId> participantByEntity = [];
        ParticipantId? viewpointParticipantId = null;
        foreach (long accountId in battleParticipants.Keys.Order())
        {
            BattleParticipantObservation battle = battleParticipants[accountId];
            arenaByAccount.TryGetValue(accountId, out ArenaParticipantObservation? arena);
            BinaryEvidence sourceEvidence = battle.Evidence;
            ParticipantId participantId = ParticipantId.New();
            int? descriptor = battle.TankCompactDescriptor;
            if (arena?.TankCompactDescriptor is not null &&
                descriptor is not null &&
                arena.TankCompactDescriptor != descriptor)
            {
                warnings.Add(
                    "An updateArena2 stats descriptor conflicted with battle results; battle results remain canonical.");
            }

            descriptor ??= arenaTankDescriptorLayoutValidated
                ? arena?.TankCompactDescriptor
                : null;
            vehicleMetadata.TryGetValue(descriptor ?? int.MinValue, out VehicleMetadata? vehicle);
            bool isViewpoint = metadata.ViewpointAccountId == accountId;
            string? fallbackTankName = isViewpoint ? metadata.ViewpointVehicleName : null;
            Participant participant = new(
                participantId,
                sessionId,
                accountId,
                arena?.EntityId,
                battle.TeamNumber ?? arena?.TeamNumber,
                battle.PlayerName ?? arena?.PlayerName,
                battle.ClanTag,
                descriptor,
                vehicle?.VehicleId ?? descriptor?.ToString(CultureInfo.InvariantCulture),
                vehicle?.DisplayName ?? fallbackTankName,
                vehicle?.TankClass ?? TankClass.Unknown,
                BotStatus.Unknown,
                EvidenceConfidence.Unknown,
                ToEvidence(request, sourceEvidence));
            participants.Add(participant);
            participantByAccount.Add(accountId, participantId);
            if (arena is not null)
            {
                if (!participantByEntity.TryAdd(arena.EntityId, participantId))
                {
                    warnings.Add(
                        "Multiple accounts map to one replay entity; the first entity mapping remains canonical.");
                }
            }

            if (isViewpoint)
            {
                viewpointParticipantId = participantId;
            }
        }

        foreach (ArenaParticipantObservation arena in arenaByEntity.Values
                     .Where(observation => observation.AccountId is null ||
                                           !battleParticipants.ContainsKey(observation.AccountId.Value))
                     .OrderBy(observation => observation.EntityId))
        {
            ParticipantId participantId = ParticipantId.New();
            int? descriptor = arenaTankDescriptorLayoutValidated
                ? arena.TankCompactDescriptor
                : null;
            vehicleMetadata.TryGetValue(
                descriptor ?? int.MinValue,
                out VehicleMetadata? vehicle);
            participants.Add(new Participant(
                participantId,
                sessionId,
                AccountId: null,
                arena.EntityId,
                arena.TeamNumber,
                arena.PlayerName,
                ClanTag: null,
                descriptor,
                vehicle?.VehicleId ??
                    descriptor?.ToString(CultureInfo.InvariantCulture),
                vehicle?.DisplayName,
                vehicle?.TankClass ?? TankClass.Unknown,
                BotStatus.Unknown,
                EvidenceConfidence.Unknown,
                ToEvidence(request, arena.Evidence)));
            if (!participantByEntity.TryAdd(arena.EntityId, participantId))
            {
                warnings.Add(
                    "Multiple updateArena2 participants map to one replay entity; the first remains canonical.");
            }
        }

        if (metadata.ViewpointAccountId is not null && viewpointParticipantId is null)
        {
            warnings.Add("The exact viewpoint account identifier was not present in the decoded roster.");
        }

        return new ParticipantProjection(
            participants,
            participantByAccount,
            participantByEntity,
            viewpointParticipantId);
    }

    private static List<PositionSample> BuildPositions(
        BattleSessionId sessionId,
        IReadOnlyDictionary<long, ParticipantId> participantByEntity,
        ReplayDecodeRequest request,
        List<PositionObservation> observations)
    {
        List<PositionSample> positions = new(observations.Count);
        foreach (PositionObservation observation in observations.OrderBy(position => position.Sequence))
        {
            participantByEntity.TryGetValue(observation.EntityId, out ParticipantId participantId);
            positions.Add(new PositionSample(
                PositionSampleId.New(),
                sessionId,
                participantId == default ? null : participantId,
                observation.EntityId,
                observation.Sequence,
                observation.ReplayTime,
                observation.X,
                observation.Y,
                observation.Z,
                NormalizedX: null,
                NormalizedY: null,
                CoordinateSpace.ReplayRaw,
                NormalizedCoordinateSpace: null,
                ToEvidence(request, observation.Evidence)));
        }

        return positions;
    }

    private static List<CanonicalEvent> BuildEvents(
        BattleSessionId sessionId,
        ParticipantProjection participantProjection,
        ReplayDecodeRequest request,
        IReadOnlyList<PositionSample> positions,
        IReadOnlyList<DamageObservation> damageEvents,
        IReadOnlyList<EventPacket> battleEndPackets)
    {
        List<EventDraft> drafts = [];
        foreach (Participant participant in participantProjection.Participants)
        {
            drafts.Add(new EventDraft(
                TimeSpan.Zero,
                long.MinValue,
                CanonicalEventKind.ParticipantObserved,
                participant.Id,
                participant.EntityId,
                JsonSerializer.Serialize(new
                {
                    participant.TeamNumber,
                    participant.VehicleCompactDescriptor,
                    botStatus = participant.BotStatus.ToString().ToLowerInvariant(),
                }),
                EvidenceConfidence.Exact,
                participant.Evidence));
        }

        foreach (PositionSample position in positions)
        {
            drafts.Add(new EventDraft(
                position.ReplayTime,
                position.Sequence,
                CanonicalEventKind.Position,
                position.ParticipantId,
                position.EntityId,
                JsonSerializer.Serialize(new
                {
                    x = position.RawX,
                    y = position.RawY,
                    z = position.RawZ,
                    coordinateSpace = "replay-raw",
                }),
                EvidenceConfidence.Exact,
                position.Evidence));
        }

        foreach (DamageObservation damage in damageEvents)
        {
            participantProjection.ParticipantByEntity.TryGetValue(
                damage.VictimEntityId,
                out ParticipantId victim);
            drafts.Add(new EventDraft(
                damage.ReplayTime,
                damage.Sequence,
                CanonicalEventKind.Damage,
                victim == default ? null : victim,
                damage.VictimEntityId,
                JsonSerializer.Serialize(new
                {
                    attackerEntityId = damage.AttackerEntityId,
                    victimEntityId = damage.VictimEntityId,
                    damage = damage.Damage,
                }),
                EvidenceConfidence.Exact,
                ToEvidence(request, damage.Evidence)));
        }

        foreach (EventPacket packet in battleEndPackets)
        {
            drafts.Add(new EventDraft(
                TimeSpan.FromSeconds(packet.ClockSeconds),
                packet.Ordinal,
                CanonicalEventKind.BattleEnded,
                ParticipantId: null,
                EntityId: null,
                "{}",
                EvidenceConfidence.Exact,
                ToEvidence(request, EventPacketDecoders.EvidenceForPacket(packet))));
        }

        List<CanonicalEvent> events = new(drafts.Count);
        long sequence = 0;
        foreach (EventDraft draft in drafts
                     .OrderBy(draft => draft.ReplayTime)
                     .ThenBy(draft => draft.SourceSequence))
        {
            events.Add(new CanonicalEvent(
                CanonicalEventId.New(),
                request.DecodeRunId,
                sessionId,
                sequence++,
                draft.Kind,
                draft.ReplayTime,
                draft.ParticipantId,
                draft.EntityId,
                draft.ValuesJson,
                draft.Confidence,
                draft.Evidence));
        }

        return events;
    }

    private static void AddRaw(
        List<RawRecord> records,
        ReplayDecodeRequest request,
        ref long ordinal,
        string kind,
        TimeSpan? replayTime,
        BinaryEvidence evidence,
        object properties)
    {
        records.Add(new RawRecord(
            RawRecordId.New(),
            request.DecodeRunId,
            ordinal++,
            kind,
            replayTime,
            ToEvidence(request, evidence),
            JsonSerializer.Serialize(properties)));
        TreaderDiagnostics.UnknownRecords.Add(1);
    }

    private static EvidenceReference ToEvidence(
        ReplayDecodeRequest request,
        BinaryEvidence evidence)
    {
        if (evidence.Length != evidence.Bytes.Length)
        {
            throw new ReplayFormatException(
                "replay.evidence_length_mismatch",
                "A decoder evidence range does not match its hashed bytes.");
        }

        return new EvidenceReference(
            request.Input.Artifact.Id,
            evidence.ArchiveEntry,
            evidence.Offset,
            evidence.Length,
            ReplayBinary.Hash(evidence.Bytes.Span));
    }

    private sealed record EnrichmentResult(
        IReadOnlyDictionary<int, VehicleMetadata> Vehicles,
        string? MapName,
        bool UsedInstalledMetadata,
        IReadOnlyList<string> Warnings)
    {
        public static EnrichmentResult Empty { get; } = new(
            new Dictionary<int, VehicleMetadata>(),
            null,
            false,
            []);
    }

    private sealed record ParticipantProjection(
        IReadOnlyList<Participant> Participants,
        IReadOnlyDictionary<long, ParticipantId> ParticipantByAccount,
        IReadOnlyDictionary<long, ParticipantId> ParticipantByEntity,
        ParticipantId? ViewpointParticipantId);

    private sealed record EventDraft(
        TimeSpan ReplayTime,
        long SourceSequence,
        CanonicalEventKind Kind,
        ParticipantId? ParticipantId,
        long? EntityId,
        string ValuesJson,
        EvidenceConfidence Confidence,
        EvidenceReference Evidence);
}
