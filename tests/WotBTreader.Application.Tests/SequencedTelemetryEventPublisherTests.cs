using WotBTreader.Application.Streaming;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class SequencedTelemetryEventPublisherTests
{
    [TestMethod]
    public async Task PublishAssignsMonotonicSequences()
    {
        SequencedTelemetryEventPublisher publisher = new(historyCapacity: 8, subscriberCapacity: 8);
        BattleSessionId sessionId = BattleSessionId.New();

        long sequence = await publisher.PublishCommittedAsync(
            sessionId,
            [CreateEvent(sessionId, 1), CreateEvent(sessionId, 2)],
            CancellationToken.None);

        Assert.AreEqual(2, sequence);
    }

    [TestMethod]
    public async Task SubscriberBehindHistoryReceivesGap()
    {
        SequencedTelemetryEventPublisher publisher = new(historyCapacity: 1, subscriberCapacity: 4);
        BattleSessionId sessionId = BattleSessionId.New();
        await publisher.PublishCommittedAsync(
            sessionId,
            [CreateEvent(sessionId, 1), CreateEvent(sessionId, 2)],
            CancellationToken.None);

        await using IAsyncEnumerator<TelemetryStreamMessage> enumerator =
            publisher.SubscribeAsync(afterSequence: 0, CancellationToken.None).GetAsyncEnumerator();

        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual(TelemetryStreamMessageKind.Gap, enumerator.Current.Kind);
    }

    private static CanonicalEvent CreateEvent(BattleSessionId sessionId, long sequence)
    {
        SourceArtifactId artifactId = SourceArtifactId.New();
        return new CanonicalEvent(
            CanonicalEventId.New(),
            DecodeRunId.New(),
            sessionId,
            sequence,
            CanonicalEventKind.Position,
            TimeSpan.FromSeconds(sequence),
            ParticipantId: null,
            EntityId: null,
            ValuesJson: "{}",
            EvidenceConfidence.Exact,
            new EvidenceReference(
                artifactId,
                "data.wotreplay",
                Offset: sequence,
                Length: 1,
                new ContentHash(new string('0', ContentHash.Sha256HexLength))));
    }
}
