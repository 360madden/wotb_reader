namespace WotBTreader.Host.Web.Services;

internal enum StreamSequenceDisposition
{
    Accepted,
    Duplicate,
    Gap,
}

/// <summary>
/// Tracks a single subscription cursor. A detected gap never guesses missing
/// events; callers must fetch a committed snapshot before accepting deltas.
/// </summary>
internal sealed class StreamSequenceTracker(long initialSequence = 0)
{
    public long LastAcceptedSequence { get; private set; } = initialSequence;

    public bool RequiresSnapshot { get; private set; }

    public StreamSequenceDisposition Observe(long sequence, bool explicitGap)
    {
        if (sequence <= LastAcceptedSequence)
        {
            return StreamSequenceDisposition.Duplicate;
        }

        if (explicitGap || sequence != LastAcceptedSequence + 1)
        {
            RequiresSnapshot = true;
            return StreamSequenceDisposition.Gap;
        }

        LastAcceptedSequence = sequence;
        return StreamSequenceDisposition.Accepted;
    }

    public void RecoverFromSnapshot(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        LastAcceptedSequence = sequence;
        RequiresSnapshot = false;
    }
}
