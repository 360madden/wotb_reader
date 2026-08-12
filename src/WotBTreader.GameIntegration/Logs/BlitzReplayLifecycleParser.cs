using System.Globalization;

namespace WotBTreader.GameIntegration.Logs;

/// <summary>
/// Reduces native log lines to an exact marker allowlist. Matching is ordinal and
/// raw or unrecognized content is discarded rather than copied into diagnostics.
/// </summary>
public sealed class BlitzReplayLifecycleParser : IBlitzReplayLifecycleParser
{
    private static readonly (string Marker, ReplayLogMarkerKind Kind)[] Markers =
    [
        ("START_REPLAY_LOCAL", ReplayLogMarkerKind.OfflineReplayStarted),
        ("STOP_REPLAY_LOCAL", ReplayLogMarkerKind.OfflineReplayStopped),
        ("Start replay event", ReplayLogMarkerKind.OfflineReplayStarted),
        // Playback completion: the game never writes STOP_REPLAY_LOCAL in a real
        // replay run (verified live 2026-08-12 on 11.19.0.10 - the only replay
        // stop markers in the session log are these post-battle controller
        // transitions to the results screen, which fire ~1-2 s after the last
        // frame). This is the FINAL-end signal: auto-loop battles chain without
        // a results screen between them (fixture 2026-08-06), so the marker only
        // appears when playback truly ends - and a session can end without it
        // entirely, which fails closed via evidence-lifetime expiry. Mapped to
        // OfflineReplayStopped so the monitor treats them as the same terminal
        // fail-closed event as an explicit stop: revoke the session and deny the
        // gate so no scan continues after playback ends.
        ("Controller activated: BattleResultsController", ReplayLogMarkerKind.OfflineReplayStopped),
        ("Controller activated: BattleResultsPersonalPageController", ReplayLogMarkerKind.OfflineReplayStopped),
        ("ReplayRecorder::StartRecording", ReplayLogMarkerKind.ReplayRecordingStarted),
        ("ReplayRecorder::StopRecording", ReplayLogMarkerKind.ReplayRecordingStopped),
    ];

    private readonly GameIntegrationOptions _options;

    /// <summary>Creates a strict, bounded lifecycle parser.</summary>
    public BlitzReplayLifecycleParser(GameIntegrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    public bool TryParse(string line, out ParsedReplayLogMarker? marker)
    {
        marker = null;
        if (string.IsNullOrEmpty(line) || line.Length > _options.MaxLogLineCharacters)
        {
            return false;
        }

        foreach ((string markerText, ReplayLogMarkerKind kind) in Markers)
        {
            if (!line.Contains(markerText, StringComparison.Ordinal))
            {
                continue;
            }

            marker = new ParsedReplayLogMarker(kind, TryParseLeadingTimestamp(line));
            return true;
        }

        return false;
    }

    private static DateTimeOffset? TryParseLeadingTimestamp(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().TrimStart();
        if (span.Length == 0)
        {
            return null;
        }

        if (span[0] == '[')
        {
            int closingBracket = span.IndexOf(']');
            if (closingBracket is > 1 and <= 40)
            {
                span = span[1..closingBracket];
            }
        }
        else
        {
            int whitespace = span.IndexOf(' ');
            if (whitespace is > 0 and <= 40)
            {
                span = span[..whitespace];
            }
            else if (span.Length > 40)
            {
                span = span[..40];
            }
        }

        return DateTimeOffset.TryParse(
            span,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
