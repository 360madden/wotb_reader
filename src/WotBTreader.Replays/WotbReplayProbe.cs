using System.Diagnostics;
using System.Security.Cryptography;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Replays;

/// <summary>
/// Performs the strict, bounded identification pass for WotB 11.18 replay archives.
/// </summary>
public sealed class WotbReplayProbe : IReplayProbe
{
    public async ValueTask<OperationResult<ReplayProbeResult>> ProbeAsync(
        ReplayInput input,
        DecoderLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        using Activity? activity = TreaderDiagnostics.ActivitySource.StartActivity("replay.probe");
        activity?.SetTag("replay.decoder", WotbReplayDecoder.DecoderId);
        try
        {
            ValidatedReplayArchive archive = await ReplayArchiveReader
                .ReadAsync(input, limits, cancellationToken)
                .ConfigureAwait(false);
            WotbReplayMetadata metadata = WotbReplayMetadata.Parse(
                archive[ReplayFormatConstants.MetadataEntry],
                limits);
            EventStreamHeader streamHeader = EventStreamReader.ReadHeader(
                archive[ReplayFormatConstants.EventStreamEntry]);

            List<string> warnings = [];
            if (!WotbReplayDecoder.IsSupportedVersion(metadata.Version))
            {
                warnings.Add("The replay game version is not supported by the strict 11.18/11.19 decoder.");
            }

            if (!EventStreamReader.IsCompatibleStreamVersion(streamHeader.ClientVersion))
            {
                warnings.Add("The event stream client version is not compatible with the strict 11.18/11.19 decoder.");
            }

            if (!string.Equals(
                    WotbReplayDecoder.NormalizeVersion(metadata.Version),
                    WotbReplayDecoder.NormalizeVersion(streamHeader.ClientVersion),
                    StringComparison.Ordinal))
            {
                warnings.Add("Metadata and event-stream client versions do not match.");
            }

            ReplayCapability capabilities =
                ReplayCapability.Metadata |
                ReplayCapability.BattleResults |
                ReplayCapability.UnknownRecordsPreserved;

            ReplayProbeResult result = new(
                IsReplay: true,
                GameVersion: metadata.Version,
                FormatVersion: $"wotbreplay.zip/{WotbReplayDecoder.NormalizeVersion(metadata.Version)}",
                ArchiveEntries: archive.Entries.Keys.Order(StringComparer.Ordinal).ToArray(),
                ObservableCapabilities: capabilities,
                Warnings: warnings);
            return OperationResult.Success(result, warnings.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Failure<ReplayProbeResult>(
                new ApplicationError(
                    "replay.cancelled",
                    "Replay probing was cancelled.",
                    Retryable: true));
        }
        catch (ReplayFormatException exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Code);
            return OperationResult.Failure<ReplayProbeResult>(
                new ApplicationError(exception.Code, exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            OverflowException or
            ArgumentException or
            CryptographicException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "replay.read_failed");
            return OperationResult.Failure<ReplayProbeResult>(
                new ApplicationError(
                    "replay.read_failed",
                    "The replay source could not be read."));
        }
    }
}
