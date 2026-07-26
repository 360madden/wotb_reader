using WotBTreader.Application.Results;

namespace WotBTreader.Application.Replay;

public sealed class ReplayDecoderRegistry
{
    private readonly IReadOnlyList<IReplayDecoder> _decoders;

    public ReplayDecoderRegistry(IEnumerable<IReplayDecoder> decoders)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        _decoders = decoders
            .OrderBy(static decoder => decoder.Descriptor.Id, StringComparer.Ordinal)
            .ThenBy(static decoder => decoder.Descriptor.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public OperationResult<IReplayDecoder> Select(ReplayProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        IReplayDecoder[] matches = _decoders.Where(decoder => decoder.CanDecode(probe)).ToArray();
        return matches.Length switch
        {
            1 => OperationResult.Success(matches[0]),
            0 => OperationResult.Failure<IReplayDecoder>(
                new ApplicationError(
                    "replay.decoder.unsupported",
                    "No registered decoder supports this replay version.")),
            _ => OperationResult.Failure<IReplayDecoder>(
                new ApplicationError(
                    "replay.decoder.ambiguous",
                    "More than one registered decoder claimed this replay.",
                    Retryable: false)),
        };
    }
}
