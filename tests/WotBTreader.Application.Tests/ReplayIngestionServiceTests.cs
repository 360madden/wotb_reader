using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Application.Streaming;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class ReplayIngestionServiceTests
{
    [TestMethod]
    public async Task EventsPublishOnlyAfterProjectionCommit()
    {
        SourceArtifact artifact = new(
            SourceArtifactId.New(),
            new ContentHash(new string('a', ContentHash.Sha256HexLength)),
            ByteLength: 4,
            MediaType: "application/vnd.wotblitz.replay",
            StoredExtension: ".wotbreplay",
            ImportedAtUtc: DateTimeOffset.UnixEpoch,
            SchemaVersion: "1");
        FakeArtifactStore store = new(artifact);
        FakeDecodeRunRepository repository = new();
        VerifyingPublisher publisher = new(repository);
        StubDecoder decoder = new(artifact);
        ReplayIngestionService service = new(
            store,
            new StubProbe(),
            new ReplayDecoderRegistry([decoder]),
            repository,
            publisher,
            TimeProvider.System,
            NullLogger<ReplayIngestionService>.Instance);

        OperationResult<ReplayIngestionOutcome> result = await service.ImportAsync(
            new ReplayIngestionRequest(
                "input.wotbreplay",
                artifact.MediaType,
                artifact.StoredExtension,
                MaximumArtifactBytes: 1024,
                DecoderLimits.Default),
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(repository.Committed);
        Assert.IsTrue(publisher.Published);
    }

    private sealed class FakeArtifactStore(SourceArtifact artifact) : ISourceArtifactStore
    {
        public ValueTask<OperationResult<SourceImportOutcome>> ImportAsync(
            SourceImportRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(new SourceImportOutcome(artifact, false)));

        public ValueTask<OperationResult<Stream>> OpenReadAsync(
            SourceArtifactId artifactId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success<Stream>(new MemoryStream([1, 2, 3, 4])));

        public ValueTask<OperationResult<SourceArtifact>> GetAsync(
            SourceArtifactId artifactId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(artifact));
    }

    private sealed class StubProbe : IReplayProbe
    {
        public ValueTask<OperationResult<ReplayProbeResult>> ProbeAsync(
            ReplayInput input,
            DecoderLimits limits,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(new ReplayProbeResult(
                IsReplay: true,
                GameVersion: "11.18.0.7",
                FormatVersion: "1",
                ArchiveEntries: ["meta.json", "data.wotreplay"],
                ReplayCapability.Metadata,
                Warnings: [])));
    }

    private sealed class StubDecoder(SourceArtifact artifact) : IReplayDecoder
    {
        public DecoderDescriptor Descriptor { get; } = new(
            "strict",
            "1",
            "1",
            new HashSet<string>(StringComparer.Ordinal) { "11.18.0.7" });

        public bool CanDecode(ReplayProbeResult probe) => probe.GameVersion == "11.18.0.7";

        public ValueTask<OperationResult<ReplayDecodeProjection>> DecodeAsync(
            ReplayDecodeRequest request,
            CancellationToken cancellationToken)
        {
            BattleSessionId sessionId = BattleSessionId.New();
            EvidenceReference evidence = new(
                artifact.Id,
                "data.wotreplay",
                Offset: 0,
                Length: 1,
                new ContentHash(new string('b', ContentHash.Sha256HexLength)));
            DecodeRun decodeRun = new(
                request.DecodeRunId,
                artifact.Id,
                Descriptor.Id,
                Descriptor.Version,
                Descriptor.SchemaVersion,
                DecodeRunStatus.Succeeded,
                ReplayCapability.Positions,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                FailureCode: null,
                FailureSummary: null);
            BattleSession session = new(
                sessionId,
                request.DecodeRunId,
                "11.18.0.7",
                ArenaIdentity: null,
                MapId: null,
                MapName: null,
                BattleTimeUtc: null,
                Duration: null,
                ViewpointParticipantId: null,
                SchemaVersion: "1");
            CanonicalEvent canonicalEvent = new(
                CanonicalEventId.New(),
                request.DecodeRunId,
                sessionId,
                Sequence: 1,
                CanonicalEventKind.Position,
                TimeSpan.Zero,
                ParticipantId: null,
                EntityId: 1,
                ValuesJson: "{}",
                EvidenceConfidence.Exact,
                evidence);
            return ValueTask.FromResult(OperationResult.Success(new ReplayDecodeProjection(
                decodeRun,
                session,
                Participants: [],
                Positions: [],
                Events: [canonicalEvent],
                RawRecords: [],
                Warnings: [])));
        }
    }

    private sealed class FakeDecodeRunRepository : IDecodeRunRepository
    {
        public bool Committed { get; private set; }

        public ValueTask<OperationResult<DecodeRun>> StartAsync(
            DecodeRun decodeRun,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OperationResult.Success(decodeRun));

        public ValueTask<OperationResult<DecodeRunSummary>> CommitAsync(
            ReplayDecodeProjection projection,
            CancellationToken cancellationToken)
        {
            Committed = true;
            return ValueTask.FromResult(OperationResult.Success(new DecodeRunSummary(
                projection.DecodeRun,
                projection.Session,
                projection.Participants.Count,
                projection.Positions.Count,
                projection.Events.Count,
                projection.RawRecords.Count)));
        }

        public ValueTask<OperationResult<DecodeRun>> FailAsync(
            DecodeRunId decodeRunId,
            DecodeRunStatus finalStatus,
            string failureCode,
            string failureSummary,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Failure path was not expected.");

        public ValueTask<OperationResult<DecodeRunSummary>> GetAsync(
            DecodeRunId decodeRunId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Query path was not expected.");
    }

    private sealed class VerifyingPublisher(FakeDecodeRunRepository repository) : ITelemetryEventPublisher
    {
        public bool Published { get; private set; }

        public ValueTask<long> PublishCommittedAsync(
            BattleSessionId battleSessionId,
            IReadOnlyList<CanonicalEvent> events,
            CancellationToken cancellationToken)
        {
            Assert.IsTrue(repository.Committed, "Events were published before transaction commit.");
            Published = true;
            return ValueTask.FromResult(1L);
        }

        public IAsyncEnumerable<TelemetryStreamMessage> SubscribeAsync(
            long afterSequence,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Subscription path was not expected.");
    }
}
