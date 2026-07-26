using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Core;

namespace WotBTreader.Application.Streaming;

public sealed class SequencedTelemetryEventPublisher : ITelemetryEventPublisher
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Channel<TelemetryStreamMessage>> _subscribers = [];
    private readonly Queue<TelemetryStreamMessage> _history;
    private readonly int _historyCapacity;
    private readonly int _subscriberCapacity;
    private readonly TimeProvider _timeProvider;
    private long _sequence;

    // Capacities carry no default values: an all-optional constructor is
    // indistinguishable from the injectable one to the DI activator, which
    // then refuses to construct this service at all.
    public SequencedTelemetryEventPublisher(int historyCapacity, int subscriberCapacity)
        : this(TimeProvider.System, historyCapacity, subscriberCapacity)
    {
    }

    public SequencedTelemetryEventPublisher(TimeProvider timeProvider)
        : this(timeProvider, historyCapacity: 4096, subscriberCapacity: 512)
    {
    }

    private SequencedTelemetryEventPublisher(
        TimeProvider timeProvider,
        int historyCapacity,
        int subscriberCapacity)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberCapacity);
        _timeProvider = timeProvider;
        _historyCapacity = historyCapacity;
        _subscriberCapacity = subscriberCapacity;
        _history = new Queue<TelemetryStreamMessage>(historyCapacity);
    }

    public ValueTask<long> PublishCommittedAsync(
        BattleSessionId battleSessionId,
        IReadOnlyList<CanonicalEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (CanonicalEvent canonicalEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long sequence = checked(++_sequence);
                TelemetryStreamMessage message = new(
                    sequence,
                    TelemetryStreamMessageKind.Event,
                    battleSessionId,
                    canonicalEvent,
                    Snapshot: null,
                    _timeProvider.GetUtcNow());
                AddToHistory(message);

                foreach (Channel<TelemetryStreamMessage> channel in _subscribers.Values)
                {
                    if (channel.Reader.CanCount && channel.Reader.Count >= _subscriberCapacity)
                    {
                        TreaderDiagnostics.DroppedStreamEvents.Add(1);
                    }

                    channel.Writer.TryWrite(message);
                }
            }

            return ValueTask.FromResult(_sequence);
        }
    }

    public async IAsyncEnumerable<TelemetryStreamMessage> SubscribeAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guid subscriptionId = Guid.CreateVersion7();
        Channel<TelemetryStreamMessage> channel = Channel.CreateBounded<TelemetryStreamMessage>(
            new BoundedChannelOptions(_subscriberCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        lock (_gate)
        {
            if (_history.Count > 0)
            {
                long earliest = _history.Peek().Sequence;
                if (afterSequence < earliest - 1)
                {
                    channel.Writer.TryWrite(new TelemetryStreamMessage(
                        earliest - 1,
                        TelemetryStreamMessageKind.Gap,
                        BattleSessionId: null,
                        Event: null,
                        Snapshot: null,
                        _timeProvider.GetUtcNow()));
                }

                foreach (TelemetryStreamMessage item in _history.Where(item => item.Sequence > afterSequence))
                {
                    channel.Writer.TryWrite(item);
                }
            }

            _subscribers.Add(subscriptionId, channel);
        }

        try
        {
            await foreach (TelemetryStreamMessage message in channel.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriptionId);
                channel.Writer.TryComplete();
            }
        }
    }

    private void AddToHistory(TelemetryStreamMessage message)
    {
        _history.Enqueue(message);
        while (_history.Count > _historyCapacity)
        {
            _history.Dequeue();
        }
    }
}
