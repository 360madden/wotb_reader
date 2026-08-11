using System.Collections.Concurrent;
using WotBTreader.Core;

namespace WotBTreader.Application.Replay;

/// <summary>
/// Caches decoded replay projections per battle session so the overlay frame
/// pipeline does not re-read every position/event/raw record from storage on
/// each frame request (the dominant cost of a single frame). Sessions are
/// immutable once decoded (every decode run creates a fresh session id), so
/// a cached projection never goes stale. The cache is bounded: only the most
/// recently requested sessions are retained, which matches how the HUD plays
/// one session at a time.
/// </summary>
public interface IProjectionCache
{
    bool TryGet(BattleSessionId sessionId, out ReplayDecodeProjection projection);
    void Store(BattleSessionId sessionId, ReplayDecodeProjection projection);
}

public sealed class ProjectionCache : IProjectionCache
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<BattleSessionId, ReplayDecodeProjection> _cache = new();
    private readonly ConcurrentQueue<BattleSessionId> _order = new();
    private readonly object _lock = new();

    public ProjectionCache(int capacity = 4)
    {
        _capacity = Math.Max(1, capacity);
    }

    public bool TryGet(BattleSessionId sessionId, out ReplayDecodeProjection projection) =>
        _cache.TryGetValue(sessionId, out projection!);

    public void Store(BattleSessionId sessionId, ReplayDecodeProjection projection)
    {
        lock (_lock)
        {
            if (_cache.ContainsKey(sessionId))
            {
                _cache[sessionId] = projection;
                return;
            }

            if (_cache.Count >= _capacity)
            {
                while (_order.TryDequeue(out BattleSessionId oldest))
                {
                    if (_cache.TryRemove(oldest, out _))
                    {
                        break;
                    }
                }
            }

            _cache[sessionId] = projection;
            _order.Enqueue(sessionId);
        }
    }
}
