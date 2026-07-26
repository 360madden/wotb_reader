using WotBTreader.Application.Capture;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Clock;

public sealed class SegmentedReplayClockSource : IReplayClockSource
{
    private readonly object _staleGate = new();
    private readonly IReplayClockSegmentRepository _repository;
    private readonly HashSet<BattleSessionId> _staleSessions = [];

    public SegmentedReplayClockSource()
        : this(new InMemoryReplayClockSegmentRepository())
    {
    }

    public SegmentedReplayClockSource(IReplayClockSegmentRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<OperationResult<ReplayClockSnapshot>> GetSnapshotAsync(
        BattleSessionId battleSessionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        OperationResult<IReadOnlyList<ReplayClockSegment>> loaded = await _repository
            .ListAsync(battleSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return OperationResult.Failure<ReplayClockSnapshot>(
                loaded.Error ?? new ApplicationError("clock.load.failed", "Replay-clock segments could not be loaded."),
                [.. loaded.Warnings]);
        }

        IReadOnlyList<ReplayClockSegment> segments = loaded.Value;
        if (segments.Count == 0)
        {
            return OperationResult.Failure<ReplayClockSnapshot>(
                new ApplicationError("clock.anchor.missing", "Replay clock has no synchronization anchor."));
        }

        ReplayClockSegment latest = segments[^1];
        if (observedAtUtc < latest.SourceAnchorUtc)
        {
            return OperationResult.Failure<ReplayClockSnapshot>(
                new ApplicationError(
                    "clock.observation.before_anchor",
                    "Observation time precedes the latest replay-clock anchor."));
        }

        TimeSpan elapsed = observedAtUtc - latest.SourceAnchorUtc;
        TimeSpan estimated = latest.ReplayAnchor + TimeSpan.FromTicks(
            checked((long)(elapsed.Ticks * latest.Speed)));
        ReplayClockSegment first = segments[0];
        TimeSpan sourceProgressAtAnchor = latest.SourceAnchorUtc - first.SourceAnchorUtc;
        TimeSpan offset = latest.ReplayAnchor - sourceProgressAtAnchor;
        ReplayClockQuality quality;
        lock (_staleGate)
        {
            quality = _staleSessions.Contains(battleSessionId)
                ? ReplayClockQuality.Stale
                : ReplayClockQuality.Estimated;
        }

        return OperationResult.Success(new ReplayClockSnapshot(
            battleSessionId,
            estimated,
            quality,
            latest.Source,
            offset,
            latest.Uncertainty,
            observedAtUtc,
            latest.SourceAnchorUtc));
    }

    public async ValueTask<OperationResult<ReplayClockSegment>> AddSegmentAsync(
        ReplayClockSegment segment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (!double.IsFinite(segment.Speed) || segment.Speed <= 0)
        {
            return OperationResult.Failure<ReplayClockSegment>(
                new ApplicationError("clock.speed.invalid", "Replay-clock speed must be finite and greater than zero."));
        }

        if (segment.ReplayAnchor < TimeSpan.Zero || segment.Uncertainty < TimeSpan.Zero)
        {
            return OperationResult.Failure<ReplayClockSegment>(
                new ApplicationError(
                    "clock.segment.invalid",
                    "Replay anchor and uncertainty must be non-negative."));
        }

        OperationResult<ReplayClockSegment> appended = await _repository
            .AppendAsync(segment, cancellationToken)
            .ConfigureAwait(false);
        if (appended.IsSuccess)
        {
            lock (_staleGate)
            {
                _staleSessions.Remove(segment.BattleSessionId);
            }
        }

        return appended;
    }

    public async ValueTask<OperationResult<ReplayClockSnapshot>> MarkStaleAsync(
        BattleSessionId battleSessionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        OperationResult<IReadOnlyList<ReplayClockSegment>> loaded = await _repository
            .ListAsync(battleSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Value is null)
        {
            return OperationResult.Failure<ReplayClockSnapshot>(
                loaded.Error ?? new ApplicationError("clock.load.failed", "Replay-clock segments could not be loaded."),
                [.. loaded.Warnings]);
        }

        if (loaded.Value.Count == 0)
        {
            return OperationResult.Failure<ReplayClockSnapshot>(
                new ApplicationError("clock.anchor.missing", "Replay clock has no synchronization anchor."));
        }

        lock (_staleGate)
        {
            _staleSessions.Add(battleSessionId);
        }

        TreaderDiagnostics.StaleReplayClocks.Add(1);
        return await GetSnapshotAsync(battleSessionId, observedAtUtc, cancellationToken).ConfigureAwait(false);
    }

    private sealed class InMemoryReplayClockSegmentRepository : IReplayClockSegmentRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<BattleSessionId, List<ReplayClockSegment>> _segments = [];

        public ValueTask<OperationResult<ReplayClockSegment>> AppendAsync(
            ReplayClockSegment segment,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_segments.TryGetValue(segment.BattleSessionId, out List<ReplayClockSegment>? segments))
                {
                    segments = [];
                    _segments.Add(segment.BattleSessionId, segments);
                }

                if (segments.Count > 0)
                {
                    ReplayClockSegment previous = segments[^1];
                    if (segment.Sequence <= previous.Sequence ||
                        segment.SourceAnchorUtc <= previous.SourceAnchorUtc ||
                        segment.ReplayAnchor < previous.ReplayAnchor)
                    {
                        return ValueTask.FromResult(OperationResult.Failure<ReplayClockSegment>(
                            new ApplicationError(
                                "clock.segment.non_monotonic",
                                "Replay-clock segments must advance sequence, source time, and replay time.")));
                    }
                }

                segments.Add(segment);
                return ValueTask.FromResult(OperationResult.Success(segment));
            }
        }

        public ValueTask<OperationResult<IReadOnlyList<ReplayClockSegment>>> ListAsync(
            BattleSessionId battleSessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<ReplayClockSegment> result =
                    _segments.TryGetValue(battleSessionId, out List<ReplayClockSegment>? segments)
                        ? segments.ToArray()
                        : [];
                return ValueTask.FromResult(OperationResult.Success(result));
            }
        }
    }
}
