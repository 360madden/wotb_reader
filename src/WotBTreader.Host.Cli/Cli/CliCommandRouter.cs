using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Capture;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
using WotBTreader.Core.Overlay;
using WotBTreader.Host.Cli.Rendering;

namespace WotBTreader.Host.Cli.Cli;

/// <summary>
/// Dispatches one CLI command to the appropriate handler. Every command
/// handler validates positional arguments, calls the relevant application
/// port, and returns a <see cref="CliExecution"/> with a machine-readable
/// JSON envelope and a human-readable message.
/// </summary>
/// <remarks>
/// <para>Output routing (stdout vs stderr) is decided by
/// <see cref="CliEntryPoint"/>, not by this class. Handlers only produce
/// the <see cref="CliExecution"/>; they never write to streams directly.</para>
/// <para>Progress for long-running commands (e.g. <c>watch</c>) is reported
/// through <see cref="ILogger{CliCommandRouter}"/>, which is configured to
/// emit to stderr so it never corrupts the stdout envelope.</para>
/// </remarks>
public sealed class CliCommandRouter
{
    private static readonly string[] CommandNames =
    [
        "doctor",
        "import",
        "inspect",
        "reprocess",
        "compare",
        "export",
        "sessions",
        "watch",
        "hp-diff",
        "yaw-diff",
        "overlay-frame",
        "overlay-strip",
        "beacon",
        "serve",
    ];

    private readonly IDoctorService _doctor;
    private readonly IReplayIngestionService _ingestion;
    private readonly IDecodeRunRepository _decodeRuns;
    private readonly ISessionQueryRepository _sessions;
    private readonly IComparisonRunRepository _comparisons;
    private readonly ITelemetryComparator _comparator;
    private readonly IHpGroundTruthProvider _hpGroundTruth;
    private readonly IYawGroundTruthProvider _yawGroundTruth;
    private readonly IOverlayFrameSource _overlayFrames;
    private readonly IBeaconStore _beacons;
    private readonly ILogger<CliCommandRouter> _logger;

    /// <summary>Creates a command router with all application ports resolved by DI.</summary>
    public CliCommandRouter(
        IDoctorService doctor,
        IReplayIngestionService ingestion,
        IDecodeRunRepository decodeRuns,
        ISessionQueryRepository sessions,
        IComparisonRunRepository comparisons,
        ITelemetryComparator comparator,
        IHpGroundTruthProvider hpGroundTruth,
        IYawGroundTruthProvider yawGroundTruth,
        IOverlayFrameSource overlayFrames,
        IBeaconStore beacons,
        ILogger<CliCommandRouter> logger)
    {
        _doctor = doctor;
        _ingestion = ingestion;
        _decodeRuns = decodeRuns;
        _sessions = sessions;
        _comparisons = comparisons;
        _comparator = comparator;
        _hpGroundTruth = hpGroundTruth;
        _yawGroundTruth = yawGroundTruth;
        _overlayFrames = overlayFrames;
        _beacons = beacons;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches the parsed invocation to the matching command handler.
    /// Unknown commands return <see cref="CliExitCode.InvalidArguments"/>;
    /// reserved-but-unavailable commands return
    /// <see cref="CliExitCode.UnsupportedCapability"/>.
    /// </summary>
    public async ValueTask<CliExecution> ExecuteAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return invocation.Command switch
        {
            "doctor" => await DoctorAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "import" => await ImportAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "inspect" => await InspectAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "reprocess" => await ReprocessAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "sessions" => await SessionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "compare" => await CompareAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "export" => await ExportAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "watch" => await WatchAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "hp-diff" => await HpDiffAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "yaw-diff" => await YawDiffAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "overlay-frame" => await OverlayFrameAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "overlay-strip" => await OverlayStripAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "beacon" => await BeaconAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "serve" => Unsupported(invocation.Command, correlationId),
            _ => Invalid(
                "cli.command.unknown",
                $"Unknown command '{invocation.Command}'. Available commands: {string.Join(", ", CommandNames)}.",
                correlationId),
        };
    }

    /// <summary>
    /// Runs the HP-diffing verdict against trusted-reader region dumps: loads
    /// the snapshots file (the pre-staged dump contract), queries the decoded
    /// session's damage ground truth, buckets the dumps, correlates in the
    /// requested mode (Lenient first — overkill), confirms under Strict, and
    /// emits the verdict per the record-diffing contract (score 1.0 +
    /// flatness 1.0 + ≥ 2 exact-sum strict matches; cross-replay agreement
    /// stays the operator-level repeatability step).
    /// </summary>
    private async ValueTask<CliExecution> HpDiffAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid(
                "cli.hp-diff.arguments",
                "hp-diff requires one positional: the snapshots JSON file path.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("session", out string? sessionText) ||
            !Guid.TryParse(sessionText, out Guid sessionGuid))
        {
            return Invalid(
                "cli.hp-diff.session",
                "hp-diff requires --session &lt;battle-session-guid&gt;.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("victim", out string? victimText) ||
            !long.TryParse(victimText, out long victimEntityId) ||
            victimEntityId <= 0)
        {
            return Invalid(
                "cli.hp-diff.victim",
                "hp-diff requires --victim &lt;entity-id&gt; (a positive integer).",
                correlationId);
        }

        DamageMatchMode matchMode = DamageMatchMode.Lenient;
        if (invocation.Options.TryGetValue("mode", out string? modeText) &&
            !Enum.TryParse(modeText, ignoreCase: true, out matchMode))
        {
            return Invalid(
                "cli.hp-diff.mode",
                "--mode must be 'strict' or 'lenient'.",
                correlationId);
        }

        DamageCorrelationDirection direction = DamageCorrelationDirection.Decrement;
        if (invocation.Options.TryGetValue("direction", out string? directionText) &&
            !Enum.TryParse(directionText, ignoreCase: true, out direction))
        {
            return Invalid(
                "cli.hp-diff.direction",
                "--direction must be 'decrement' or 'increment' (default: decrement/HP).",
                correlationId);
        }

        // Static evidence (VerifyPlayerHpChain, 11.19.0.10) pins HP as a
        // SIGNED int16 at [entity+0xB8] on the entity base record, so the
        // HP path must scan int16 candidates or it can never find the field.
        // Off by default so damage-dealt (int32 counter) callers are
        // unchanged.
        bool includeInt16 = direction == DamageCorrelationDirection.Decrement;
        if (invocation.Options.TryGetValue("int16", out string? int16Text) &&
            bool.TryParse(int16Text, out bool parsedInt16))
        {
            includeInt16 = parsedInt16;
        }

        OperationResult<IReadOnlyList<RecordSnapshot>> snapshotsResult =
            HpDiffSnapshotsFile.Load(invocation.Positionals[0]);
        if (!snapshotsResult.IsSuccess || snapshotsResult.Value is null)
        {
            return FromResult(snapshotsResult, correlationId, "Snapshots loaded.");
        }

        OperationResult<HpGroundTruth> groundTruthResult = await _hpGroundTruth
            .GetAsync(new BattleSessionId(sessionGuid), cancellationToken)
            .ConfigureAwait(false);
        if (!groundTruthResult.IsSuccess || groundTruthResult.Value is null)
        {
            return FromResult(groundTruthResult, correlationId, "Ground truth loaded.");
        }

        IReadOnlyList<ByteChangeWindow> windows =
            RecordChangeBucketer.Bucket(snapshotsResult.Value);
        IReadOnlyList<HpDamageEvent> events = groundTruthResult.Value.Events;
        IReadOnlyList<DamageCorrelationCandidate> primary = HpDamageCorrelator.Correlate(
            windows, events, victimEntityId, matchMode, direction, includeInt16);
        IReadOnlyList<DamageCorrelationCandidate> confirm = HpDamageCorrelator.Correlate(
            windows, events, victimEntityId, DamageMatchMode.Strict, direction, includeInt16);

        DamageCorrelationCandidate? top = primary.Count > 0 ? primary[0] : null;
        DamageCorrelationCandidate? strictTop = confirm.Count > 0 ? confirm[0] : null;

        bool hit = top is not null
            && top.Score >= 1.0 - 1e-9
            && top.MatchedDamageWindows >= 2
            && top.Flatness >= 1.0 - 1e-9
            && strictTop is not null
            && strictTop.Offset == top.Offset
            && strictTop.MatchedDamageWindows >= 2;

        string reason;
        if (hit)
        {
            reason = "HIT: score 1.0, flatness 1.0, >= 2 exact-sum Strict matches";
        }
        else if (top is null)
        {
            reason = "no candidate matched any damage window";
        }
        else if (top.Score < 1.0 - 1e-9)
        {
            reason = $"top candidate score {top.Score:0.###} < 1.0";
        }
        else if (top.MatchedDamageWindows < 2)
        {
            reason = $"only {top.MatchedDamageWindows} matched window(s); need >= 2";
        }
        else if (top.Flatness < 1.0 - 1e-9)
        {
            reason = $"top candidate flatness {top.Flatness:0.###} < 1.0 (changed in control windows)";
        }
        else if (strictTop is null)
        {
            reason = "no Strict confirmation (no exact-sum matches)";
        }
        else if (strictTop.Offset != top.Offset)
        {
            reason = $"Strict top candidate is a different offset (0x{strictTop.Offset:X} vs 0x{top.Offset:X})";
        }
        else if (strictTop.MatchedDamageWindows < 2)
        {
            reason = $"Strict confirmation has only {strictTop.MatchedDamageWindows} exact match(es); need >= 2";
        }
        else
        {
            reason = "HIT: score 1.0, flatness 1.0, >= 2 exact-sum Strict matches";
        }

        object data = new
        {
            command = "hp-diff",
            sessionId = sessionGuid,
            victimEntityId,
            mode = matchMode.ToString().ToLowerInvariant(),
            direction = direction.ToString().ToLowerInvariant(),
            snapshots = snapshotsResult.Value.Count,
            changeWindows = windows.Count,
            damageWindows = top?.TotalDamageWindows ?? 0,
            topCandidate = top is null
                ? null
                : new
                {
                    offset = top.Offset,
                    score = top.Score,
                    matchedDamageWindows = top.MatchedDamageWindows,
                    totalDamageWindows = top.TotalDamageWindows,
                    changedWindows = top.ChangedWindows,
                    flatness = top.Flatness,
                    controlWindows = top.ControlWindows,
                    changedControlWindows = top.ChangedControlWindows,
                    matchedWindows = top.MatchedWindows?
                        .Select(matched => new
                        {
                            fromSeconds = matched.FromReplayTime.TotalSeconds,
                            toSeconds = matched.ToReplayTime.TotalSeconds,
                            damageSum = matched.DamageSum,
                        })
                        .ToList(),
                    explanation = top.Explanation,
                },
            strictConfirmation = strictTop is null
                ? null
                : new
                {
                    offset = strictTop.Offset,
                    matchedDamageWindows = strictTop.MatchedDamageWindows,
                    totalDamageWindows = strictTop.TotalDamageWindows,
                },
            verdict = new
            {
                hit,
                reason,
            },
        };

        return Success(
            data,
            hit
                ? "HP field identified (candidate offset matches the damage timeline)."
                : "No HP field confirmed.",
            correlationId);
    }

    /// <summary>
    /// Facing (yaw) verdict command — the mirror of <c>hp-diff</c> for the
    /// packet-derived rotation ground truth: buckets the trusted-reader region
    /// dumps (same snapshots schema), correlates a wrap-aware float32 field
    /// against the target entity's <c>position_samples.yaw</c> deltas, and
    /// emits the hardened contract (score 1.0 + flatness 1.0 + ≥ 2 matched
    /// turn windows; stationary control windows must be unchanged).
    /// </summary>
    private async ValueTask<CliExecution> YawDiffAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid(
                "cli.yaw-diff.arguments",
                "yaw-diff requires one positional: the snapshots JSON file path.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("session", out string? sessionText) ||
            !Guid.TryParse(sessionText, out Guid sessionGuid))
        {
            return Invalid(
                "cli.yaw-diff.session",
                "yaw-diff requires --session &lt;battle-session-guid&gt;.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("victim", out string? victimText) ||
            !long.TryParse(victimText, out long entityId) ||
            entityId <= 0)
        {
            return Invalid(
                "cli.yaw-diff.victim",
                "yaw-diff requires --victim &lt;entity-id&gt; (a positive integer).",
                correlationId);
        }

        double tolerance = HeadingCorrelator.DefaultToleranceRadians;
        if (invocation.Options.TryGetValue("tolerance", out string? toleranceText) &&
            (!double.TryParse(toleranceText, out tolerance) ||
             !double.IsFinite(tolerance) ||
             tolerance <= 0))
        {
            return Invalid(
                "cli.yaw-diff.tolerance",
                "--tolerance must be a positive finite number of radians.",
                correlationId);
        }

        OperationResult<IReadOnlyList<RecordSnapshot>> snapshotsResult =
            HpDiffSnapshotsFile.Load(invocation.Positionals[0]);
        if (!snapshotsResult.IsSuccess || snapshotsResult.Value is null)
        {
            return FromResult(snapshotsResult, correlationId, "Snapshots loaded.");
        }

        OperationResult<YawGroundTruth> groundTruthResult = await _yawGroundTruth
            .GetAsync(new BattleSessionId(sessionGuid), cancellationToken)
            .ConfigureAwait(false);
        if (!groundTruthResult.IsSuccess || groundTruthResult.Value is null)
        {
            return FromResult(groundTruthResult, correlationId, "Ground truth loaded.");
        }

        IReadOnlyList<ByteChangeWindow> windows =
            RecordChangeBucketer.Bucket(snapshotsResult.Value);
        IReadOnlyList<HeadingCorrelationCandidate> candidates =
            HeadingCorrelator.Correlate(
                windows,
                groundTruthResult.Value.Samples,
                entityId,
                tolerance);

        HeadingCorrelationCandidate? top = candidates.Count > 0 ? candidates[0] : null;
        bool hit = top is not null
            && top.Score >= 1.0 - 1e-9
            && top.MatchedWindows >= 2
            && top.Flatness >= 1.0 - 1e-9;

        string reason;
        if (hit)
        {
            reason = "HIT: score 1.0, flatness 1.0, >= 2 matched turn windows";
        }
        else if (top is null)
        {
            reason = "no candidate matched any turn window";
        }
        else if (top.Score < 1.0 - 1e-9)
        {
            reason = $"top candidate score {top.Score:0.###} < 1.0";
        }
        else if (top.MatchedWindows < 2)
        {
            reason = $"only {top.MatchedWindows} matched window(s); need >= 2";
        }
        else
        {
            reason = $"top candidate flatness {top.Flatness:0.###} < 1.0 (changed in stationary control windows)";
        }

        object data = new
        {
            command = "yaw-diff",
            sessionId = sessionGuid,
            entityId,
            tolerance,
            snapshots = snapshotsResult.Value.Count,
            changeWindows = windows.Count,
            yawSamples = groundTruthResult.Value.Samples.Count,
            turnWindows = top?.TotalWindows ?? 0,
            topCandidate = top is null
                ? null
                : new
                {
                    offset = top.Offset,
                    score = top.Score,
                    matchedWindows = top.MatchedWindows,
                    totalWindows = top.TotalWindows,
                    changedWindows = top.ChangedWindows,
                    flatness = top.Flatness,
                    controlWindows = top.ControlWindows,
                    changedControlWindows = top.ChangedControlWindows,
                    matchedWindowList = top.MatchedWindowList?
                        .Select(matched => new
                        {
                            fromSeconds = matched.FromReplayTime.TotalSeconds,
                            toSeconds = matched.ToReplayTime.TotalSeconds,
                            expectedDeltaRadians = matched.ExpectedDeltaRadians,
                        })
                        .ToList(),
                    explanation = top.Explanation,
                },
            verdict = new
            {
                hit,
                reason,
            },
        };

        return Success(
            data,
            hit
                ? "Yaw field identified (candidate offset matches the packet-derived rotation timeline)."
                : "No yaw field confirmed.",
            correlationId);
    }

    /// <summary>
    /// Renders one overlay frame from decoded replay data at a replay time:
    /// the viewpoint camera plus every tank with position evidence, each
    /// projected to viewport pixels via <see cref="WorldToScreen"/>. Pure
    /// offline preview of the replay-overlay data seam — no UI, no process
    /// access. Options: --fov (vertical degrees, default 90), --width,
    /// --height (viewport pixels, default 1920x1080), --png &lt;path&gt;
    /// (also write a schematic PNG preview of the projected frame).
    /// </summary>
    private async ValueTask<CliExecution> OverlayFrameAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1
            || !double.TryParse(invocation.Positionals[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double replayTimeSeconds)
            || replayTimeSeconds < 0)
        {
            return Invalid(
                "cli.overlay-frame.time",
                "overlay-frame requires one positional: the replay time in seconds (>= 0).",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("session", out string? sessionText) ||
            !Guid.TryParse(sessionText, out Guid sessionGuid))
        {
            return Invalid(
                "cli.overlay-frame.session",
                "overlay-frame requires --session &lt;battle-session-guid&gt;.",
                correlationId);
        }

        double fovDegrees = 90.0;
        if (invocation.Options.TryGetValue("fov", out string? fovText) &&
            (!double.TryParse(fovText, NumberStyles.Float, CultureInfo.InvariantCulture, out fovDegrees)
             || fovDegrees <= 0 || fovDegrees >= 180))
        {
            return Invalid(
                "cli.overlay-frame.fov",
                "--fov must be a positive number of degrees below 180.",
                correlationId);
        }

        double viewportWidth = 1920.0;
        if (invocation.Options.TryGetValue("width", out string? widthText) &&
            (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out viewportWidth)
             || viewportWidth <= 0))
        {
            return Invalid(
                "cli.overlay-frame.width",
                "--width must be a positive viewport width in pixels.",
                correlationId);
        }

        double viewportHeight = 1080.0;
        if (invocation.Options.TryGetValue("height", out string? heightText) &&
            (!double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out viewportHeight)
             || viewportHeight <= 0))
        {
            return Invalid(
                "cli.overlay-frame.height",
                "--height must be a positive viewport height in pixels.",
                correlationId);
        }

        string? pngPath = null;
        if (invocation.Options.TryGetValue("png", out string? pngText))
        {
            if (string.IsNullOrWhiteSpace(pngText))
            {
                return Invalid(
                    "cli.overlay-frame.png",
                    "--png requires a destination file path.",
                    correlationId);
            }

            pngPath = Path.GetFullPath(pngText);
        }

        OperationResult<OverlayFrameProjection> projectionResult = await BuildOverlayProjectionAsync(
            sessionGuid,
            replayTimeSeconds,
            fovDegrees,
            viewportWidth,
            viewportHeight,
            cancellationToken).ConfigureAwait(false);
        if (!projectionResult.IsSuccess || projectionResult.Value is null)
        {
            return FromResult(projectionResult, correlationId, "Overlay frame built.");
        }

        OverlayFrameProjection projection = projectionResult.Value;
        if (pngPath is not null)
        {
            CliExecution? writeFailure = await WritePngAsync(
                pngPath,
                projection,
                (int)viewportWidth,
                (int)viewportHeight,
                sessionGuid,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            if (writeFailure is not null)
            {
                return writeFailure;
            }
        }

        object data = new
        {
            command = "overlay-frame",
            sessionId = sessionGuid,
            replayTimeSeconds,
            fovDegrees,
            viewportWidth,
            viewportHeight,
            pngPath,
            camera = new
            {
                x = projection.CameraX,
                y = projection.CameraY,
                z = projection.CameraZ,
                yawRadians = projection.CameraYawRadians,
                pitchRadians = projection.CameraPitchRadians,
            },
            tanks = projection.Tanks.Select(tank => new
            {
                tank.EntityId,
                tank.PlayerName,
                tank.TankName,
                tank.ClanTag,
                tank.TeamNumber,
                tank.HpFraction,
                tank.Alive,
                tank.DistanceMeters,
                tank.WorldX,
                tank.WorldZ,
                tank.DamageDealt,
                tank.DamageTaken,
                tank.Kills,
                tank.MaxHealth,
                tank.CurrentHealth,
                screen = tank.ScreenX is null
                    ? null
                    : new
                    {
                        x = tank.ScreenX,
                        y = tank.ScreenY,
                        depth = tank.Depth,
                        inViewport = tank.InViewport,
                        screenHeadingDegrees = tank.ScreenHeadingDegrees,
                    },
            }),
            beacons = projection.Beacons.Select(beacon => new
            {
                beacon.Name,
                beacon.Color,
                beacon.DistanceMeters,
                beacon.WorldX,
                beacon.WorldZ,
                screen = beacon.ScreenX is null
                    ? null
                    : new
                    {
                        x = beacon.ScreenX,
                        y = beacon.ScreenY,
                        depth = beacon.Depth,
                        inViewport = beacon.InViewport,
                    },
            }),
            pips = projection.Pips.Select(pip => new
            {
                pip.EntityId,
                kind = pip.Kind.ToString(),
                pip.Damage,
                pip.ScreenX,
                pip.ScreenY,
            }),
            kills = projection.Kills.Select(kill => new
            {
                kill.VictimEntityId,
                kill.KillerEntityId,
                replayTimeSeconds = kill.ReplayTime.TotalSeconds,
            }),
        };

        return Success(
            data,
            $"Overlay frame at {replayTimeSeconds:0.###}s: {projection.Tanks.Count} tank(s), {projection.Beacons.Count} beacon(s) projected.",
            correlationId);
    }

    /// <summary>Builds one projected overlay frame at a replay time (frame
    /// source → beacons → <see cref="OverlayFrameProjector"/>).</summary>
    private async ValueTask<OperationResult<OverlayFrameProjection>> BuildOverlayProjectionAsync(
        Guid sessionGuid,
        double replayTimeSeconds,
        double fovDegrees,
        double viewportWidth,
        double viewportHeight,
        CancellationToken cancellationToken)
    {
        OperationResult<OverlayFrame> frameResult = await _overlayFrames.GetFrameAsync(
            new BattleSessionId(sessionGuid),
            TimeSpan.FromSeconds(replayTimeSeconds),
            cancellationToken).ConfigureAwait(false);
        if (!frameResult.IsSuccess || frameResult.Value is null)
        {
            return OperationResult.Failure<OverlayFrameProjection>(
                frameResult.Error ?? new ApplicationError(
                    "cli.overlay-frame.missing",
                    "The session has no overlay frame at this time."));
        }

        IReadOnlyList<OverlayBeacon> beacons = await _beacons.GetBeaconsAsync(
            new BattleSessionId(sessionGuid),
            cancellationToken).ConfigureAwait(false);
        double fovRadians = fovDegrees * Math.PI / 180.0;
        return OperationResult.Success(OverlayFrameProjector.Project(
            frameResult.Value, fovRadians, viewportWidth, viewportHeight, beacons));
    }

    /// <summary>Renders one projection to a PNG at <paramref name="pngPath"/>
    /// (with the session's minimap boundary when resolvable). Returns a
    /// <see cref="CliExecution"/> failure on write errors, else null.</summary>
    private async ValueTask<CliExecution?> WritePngAsync(
        string pngPath,
        OverlayFrameProjection projection,
        int width,
        int height,
        Guid sessionGuid,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        MapBoundary? boundary = await ResolveMapBoundaryAsync(sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        byte[] rgba = FrameRasterizer.Render(projection, width, height, boundary);
        byte[] png = PngEncoder.Encode(width, height, rgba);
        try
        {
            await File.WriteAllBytesAsync(pngPath, png, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                "cli.overlay-frame.png_write",
                $"--png could not write {pngPath}: {exception.Message}",
                correlationId);
        }
    }

    /// <summary>
    /// Renders a contact sheet of <c>count</c> evenly spaced overlay frames
    /// from <c>start</c> to <c>end</c> seconds into one PNG: the W2S view of
    /// the whole battle in a single image for offline motion/occlusion
    /// review. Cells are fixed 640x360 (16:9); the grid is as square as
    /// possible (columns = ceil(sqrt(count))). Requires --session and --png.
    /// </summary>
    private async ValueTask<CliExecution> OverlayStripAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 3
            || !double.TryParse(invocation.Positionals[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double startSeconds)
            || startSeconds < 0
            || !double.TryParse(invocation.Positionals[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double endSeconds)
            || endSeconds <= startSeconds
            || !int.TryParse(invocation.Positionals[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count < 1 || count > 64)
        {
            return Invalid(
                "cli.overlay-strip.args",
                "overlay-strip requires: <start> <end> <count> (count 1..64, end > start >= 0).",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("session", out string? sessionText) ||
            !Guid.TryParse(sessionText, out Guid sessionGuid))
        {
            return Invalid(
                "cli.overlay-strip.session",
                "overlay-strip requires --session <battle-session-guid>.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("png", out string? pngText)
            || string.IsNullOrWhiteSpace(pngText))
        {
            return Invalid(
                "cli.overlay-strip.png",
                "overlay-strip requires --png <destination-file-path>.",
                correlationId);
        }

        string pngPath = Path.GetFullPath(pngText);
        const double cellWidth = 640;
        const double cellHeight = 360;
        var projections = new List<OverlayFrameProjection>(count);
        for (int index = 0; index < count; index++)
        {
            double timeSeconds = count == 1
                ? startSeconds
                : startSeconds + (endSeconds - startSeconds) * index / (count - 1);
            OperationResult<OverlayFrameProjection> projectionResult = await BuildOverlayProjectionAsync(
                sessionGuid,
                timeSeconds,
                fovDegrees: 90.0,
                cellWidth,
                cellHeight,
                cancellationToken).ConfigureAwait(false);
            if (!projectionResult.IsSuccess || projectionResult.Value is null)
            {
                return FromResult(projectionResult, correlationId, "Overlay strip built.");
            }

            projections.Add(projectionResult.Value);
        }

        MapBoundary? boundary = await ResolveMapBoundaryAsync(sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        byte[] sheet = FrameRasterizer.RenderContactSheet(
            projections, boundary, (int)cellWidth, (int)cellHeight);
        byte[] png = PngEncoder.Encode(
            FrameRasterizer.ContactSheetWidth(projections.Count, (int)cellWidth),
            FrameRasterizer.ContactSheetHeight(projections.Count, (int)cellHeight),
            sheet);
        try
        {
            await File.WriteAllBytesAsync(pngPath, png, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                "cli.overlay-strip.png_write",
                $"--png could not write {pngPath}: {exception.Message}",
                correlationId);
        }

        return Success(
            new
            {
                command = "overlay-strip",
                sessionId = sessionGuid,
                startSeconds,
                endSeconds,
                count,
                pngPath,
                columns = FrameRasterizer.ContactSheetColumns(projections.Count),
                rows = FrameRasterizer.ContactSheetRows(projections.Count),
                cellWidth,
                cellHeight,
            },
            $"Overlay strip: {count} frame(s) from {startSeconds:0.###}s to {endSeconds:0.###}s written to {pngPath}.",
            correlationId);
    }

    /// <summary>Resolves the session's map boundary (null when the session
    /// carries no map ID or no position-derived boundary exists yet) for the
    /// PNG preview's god-view minimap inset.</summary>
    private async ValueTask<MapBoundary?> ResolveMapBoundaryAsync(
        Guid sessionGuid,
        CancellationToken cancellationToken)
    {
        OperationResult<ReplayDecodeProjection> projectionResult = await _sessions
            .GetProjectionAsync(new BattleSessionId(sessionGuid), cancellationToken)
            .ConfigureAwait(false);
        string? mapId = projectionResult.Value?.Session?.MapId;
        if (mapId is null)
        {
            return null;
        }

        IReadOnlyList<MapBoundary> boundaries = await _sessions
            .GetMapBoundariesAsync(cancellationToken).ConfigureAwait(false);
        return boundaries.FirstOrDefault(boundary => boundary.MapId == mapId);
    }

    /// <summary>
    /// Manages persistent overlay beacons (world-space POIs) for a session.
    /// Subcommands: <c>beacon list --session &lt;guid&gt;</c>,
    /// <c>beacon add &lt;name&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; --session &lt;guid&gt;
    /// [--color #RRGGBB] [--from &lt;seconds&gt;] [--until &lt;seconds&gt;]</c>,
    /// and <c>beacon remove &lt;name&gt; --session &lt;guid&gt;</c>. Coordinates are
    /// decoded-replay world units — read them from <c>overlay-frame</c> or the
    /// session's position data. Beacons are offline data: placed against the
    /// replay, never read from a game process.
    /// </summary>
    private async ValueTask<CliExecution> BeaconAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count < 1)
        {
            return Invalid(
                "cli.beacon.subcommand",
                "beacon requires a subcommand: list, add, or remove.",
                correlationId);
        }

        if (!invocation.Options.TryGetValue("session", out string? sessionText) ||
            !Guid.TryParse(sessionText, out Guid sessionGuid))
        {
            return Invalid(
                "cli.beacon.session",
                "beacon requires --session &lt;battle-session-guid&gt;.",
                correlationId);
        }

        BattleSessionId sessionId = new(sessionGuid);
        string subcommand = invocation.Positionals[0].ToLowerInvariant();
        switch (subcommand)
        {
            case "list":
                return await BeaconListAsync(sessionId, invocation, correlationId, cancellationToken).ConfigureAwait(false);
            case "add":
                return await BeaconAddAsync(sessionId, invocation, correlationId, cancellationToken).ConfigureAwait(false);
            case "remove":
                return await BeaconRemoveAsync(sessionId, invocation, correlationId, cancellationToken).ConfigureAwait(false);
            default:
                return Invalid(
                    "cli.beacon.subcommand.unknown",
                    $"Unknown beacon subcommand '{invocation.Positionals[0]}'. Expected list, add, or remove.",
                    correlationId);
        }
    }

    private async ValueTask<CliExecution> BeaconListAsync(
        BattleSessionId sessionId,
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid("cli.beacon.list.arguments", "beacon list accepts no positional arguments.", correlationId);
        }

        IReadOnlyList<OverlayBeacon> beacons = await _beacons.GetBeaconsAsync(
            sessionId, cancellationToken).ConfigureAwait(false);
        object data = new
        {
            command = "beacon list",
            sessionId = sessionId.Value,
            count = beacons.Count,
            beacons = beacons.Select(beacon => new
            {
                beacon.Name,
                beacon.X,
                beacon.Y,
                beacon.Z,
                beacon.Color,
                visibleFromSeconds = beacon.VisibleFrom?.TotalSeconds,
                visibleUntilSeconds = beacon.VisibleUntil?.TotalSeconds,
            }),
        };
        return Success(data, $"{beacons.Count} beacon(s) for session {sessionId.Value}.", correlationId);
    }

    private async ValueTask<CliExecution> BeaconAddAsync(
        BattleSessionId sessionId,
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 5
            || !double.TryParse(invocation.Positionals[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(invocation.Positionals[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
            || !double.TryParse(invocation.Positionals[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            return Invalid(
                "cli.beacon.add.arguments",
                "beacon add requires: <name> <x> <y> <z> --session <guid> [--color #RRGGBB] [--from <s>] [--until <s>].",
                correlationId);
        }

        string name = invocation.Positionals[1];
        string color = invocation.Options.TryGetValue("color", out string? colorText) && colorText is not null
            ? colorText
            : "#FFD700";
        TimeSpan? visibleFrom = null;
        TimeSpan? visibleUntil = null;
        if (invocation.Options.TryGetValue("from", out string? fromText) && fromText is not null)
        {
            if (!double.TryParse(fromText, NumberStyles.Float, CultureInfo.InvariantCulture, out double fromSeconds)
                || fromSeconds < 0)
            {
                return Invalid("cli.beacon.add.from", "--from must be a non-negative number of seconds.", correlationId);
            }

            visibleFrom = TimeSpan.FromSeconds(fromSeconds);
        }

        if (invocation.Options.TryGetValue("until", out string? untilText) && untilText is not null)
        {
            if (!double.TryParse(untilText, NumberStyles.Float, CultureInfo.InvariantCulture, out double untilSeconds)
                || untilSeconds < 0)
            {
                return Invalid("cli.beacon.add.until", "--until must be a non-negative number of seconds.", correlationId);
            }

            visibleUntil = TimeSpan.FromSeconds(untilSeconds);
        }

        await _beacons.AddBeaconAsync(
            sessionId,
            new OverlayBeacon(name, x, y, z, color, visibleFrom, visibleUntil),
            cancellationToken).ConfigureAwait(false);

        object data = new
        {
            command = "beacon add",
            sessionId = sessionId.Value,
            name,
            x,
            y,
            z,
            color,
            visibleFromSeconds = visibleFrom?.TotalSeconds,
            visibleUntilSeconds = visibleUntil?.TotalSeconds,
        };
        return Success(data, $"Beacon '{name}' saved for session {sessionId.Value}.", correlationId);
    }

    private async ValueTask<CliExecution> BeaconRemoveAsync(
        BattleSessionId sessionId,
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2)
        {
            return Invalid(
                "cli.beacon.remove.arguments",
                "beacon remove requires: <name> --session <guid>.",
                correlationId);
        }

        string name = invocation.Positionals[1];
        bool removed = await _beacons.RemoveBeaconAsync(
            sessionId, name, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return Invalid("cli.beacon.remove.missing", $"No beacon named '{name}' for this session.", correlationId);
        }

        object data = new
        {
            command = "beacon remove",
            sessionId = sessionId.Value,
            name,
        };
        return Success(data, $"Beacon '{name}' removed.", correlationId);
    }

    /// <summary>Runs non-mutating health checks and returns the report.</summary>
    private async ValueTask<CliExecution> DoctorAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 0)
        {
            return Invalid("cli.doctor.arguments", "doctor accepts no positional arguments.", correlationId);
        }

        DoctorReport report = await _doctor.RunAsync(cancellationToken).ConfigureAwait(false);
        bool healthy = report.Checks.Where(static check => check.Required)
            .All(static check => string.Equals(check.Status, "pass", StringComparison.Ordinal));
        return healthy
            ? Success(report, "Doctor checks passed.", correlationId)
            : Failure(
                CliExitCode.InvalidInput,
                "doctor.required_check_failed",
                "One or more required doctor checks failed.",
                data: report,
                correlationId);
    }

    /// <summary>
    /// Imports one <c>.wotbreplay</c> file into content-addressed storage
    /// and decodes it. The same file imported twice produces two distinct
    /// decode runs sharing one artifact (evidence-first reprocessing rule).
    /// </summary>
    private async ValueTask<CliExecution> ImportAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid("cli.import.path_required", "import requires exactly one replay path.", correlationId);
        }

        string candidatePath = invocation.Positionals[0];
        if (!string.Equals(Path.GetExtension(candidatePath), ".wotbreplay", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(
                "cli.import.extension",
                "import accepts a .wotbreplay file.",
                correlationId,
                CliExitCode.InvalidInput);
        }

        OperationResult<ReplayIngestionOutcome> result = await _ingestion.ImportAsync(
            new ReplayIngestionRequest(
                candidatePath,
                "application/vnd.wotblitz.replay",
                ".wotbreplay",
                MaximumArtifactBytes: 128 * 1024 * 1024,
                DecoderLimits.Default),
            cancellationToken).ConfigureAwait(false);

        return FromResult(
            result,
            correlationId,
            result.Value is null
                ? "Replay import failed."
                : $"Imported artifact {result.Value.Artifact.Id}; decode run {result.Value.DecodeRun.DecodeRun.Id}.");
    }

    /// <summary>Looks up one decode run by its GUID and returns the summary.</summary>
    private async ValueTask<CliExecution> InspectAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1 ||
            !Guid.TryParse(invocation.Positionals[0], out Guid value))
        {
            return Invalid(
                "cli.inspect.decode_run_id",
                "inspect requires one decode-run GUID.",
                correlationId);
        }

        OperationResult<DecodeRunSummary> result = await _decodeRuns
            .GetAsync(new DecodeRunId(value), cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Decode run loaded.");
    }

    /// <summary>
    /// Re-decodes a source artifact that was previously imported, creating
    /// a new decode run. Useful after a decoder update to reprocess old
    /// replays with the latest logic.
    /// </summary>
    private async ValueTask<CliExecution> ReprocessAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1 ||
            !Guid.TryParse(invocation.Positionals[0], out Guid value))
        {
            return Invalid(
                "cli.reprocess.artifact_id",
                "reprocess requires one source-artifact GUID.",
                correlationId);
        }

        OperationResult<ReplayIngestionOutcome> result = await _ingestion
            .ReprocessAsync(new SourceArtifactId(value), DecoderLimits.Default, cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Replay reprocessing completed.");
    }

    /// <summary>
    /// Lists decoded battle sessions with offset/limit paging (default 50,
    /// max 200). Use <c>--offset</c> and <c>--limit</c> options.
    /// </summary>
    private async ValueTask<CliExecution> SessionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 0)
        {
            return Invalid("cli.sessions.arguments", "sessions accepts no positional arguments.", correlationId);
        }

        if (!TryGetInteger(invocation.Options, "offset", defaultValue: 0, minimum: 0, maximum: int.MaxValue, out int offset) ||
            !TryGetInteger(invocation.Options, "limit", defaultValue: 50, minimum: 1, maximum: 200, out int limit))
        {
            return Invalid(
                "cli.sessions.range",
                "sessions --offset must be non-negative and --limit must be between 1 and 200.",
                correlationId);
        }

        IReadOnlyList<DecodeRunSummary> sessions = await _sessions
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);
        return Success(sessions, $"Loaded {sessions.Count} session(s).", correlationId);
    }

    /// <summary>
    /// Dispatches comparison sub-commands. Supported: <c>list</c> (paged list
    /// of comparison runs), <c>inspect</c> (full result for one run),
    /// <c>create</c> (compare two battle sessions).
    /// </summary>
    private async ValueTask<CliExecution> CompareAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count == 0)
        {
            return Invalid(
                "cli.compare.subcommand_required",
                "Usage: compare list | compare inspect <comparison-run-id> | compare create <left-session-id> <right-session-id>.",
                correlationId);
        }

        string subCommand = invocation.Positionals[0];
        return subCommand switch
        {
            "list" => await CompareListAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "inspect" => await CompareInspectAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "create" => await CompareCreateAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            _ => Invalid(
                "cli.compare.unknown_subcommand",
                $"Unknown compare subcommand '{subCommand}'. Available: list, inspect, create.",
                correlationId),
        };
    }

    /// <summary>Lists comparison runs with paging (default 50, max 200).</summary>
    private async ValueTask<CliExecution> CompareListAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!TryGetInteger(invocation.Options, "offset", defaultValue: 0, minimum: 0, maximum: int.MaxValue, out int offset) ||
            !TryGetInteger(invocation.Options, "limit", defaultValue: 50, minimum: 1, maximum: 200, out int limit))
        {
            return Invalid(
                "cli.compare.list.range",
                "compare list --offset must be non-negative and --limit must be between 1 and 200.",
                correlationId);
        }

        IReadOnlyList<ComparisonRun> runs = await _comparisons
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);
        return Success(runs, $"Loaded {runs.Count} comparison run(s).", correlationId);
    }

    /// <summary>Returns the full comparison result (metadata, summary, items) for one run.</summary>
    private async ValueTask<CliExecution> CompareInspectAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.compare.inspect.id_required",
                "compare inspect requires one comparison-run GUID.",
                correlationId);
        }

        OperationResult<TelemetryComparison> result = await _comparisons
            .GetAsync(new ComparisonRunId(value), cancellationToken)
            .ConfigureAwait(false);
        return FromResult(result, correlationId, "Comparison run loaded.");
    }

    /// <summary>
    /// Compares the telemetry events of two decoded battle sessions and
    /// persists the result as a new comparison run. Returns the new
    /// comparison run ID and summary on success.
    /// </summary>
    private async ValueTask<CliExecution> CompareCreateAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 3 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid leftGuid) ||
            !Guid.TryParse(invocation.Positionals[2], out Guid rightGuid))
        {
            return Invalid(
                "cli.compare.create.two_ids_required",
                "compare create requires two battle-session GUIDs (left and right).",
                correlationId);
        }

        BattleSessionId leftSessionId = new(leftGuid);
        BattleSessionId rightSessionId = new(rightGuid);

        OperationResult<ReplayDecodeProjection> leftResult = await _sessions
            .GetProjectionAsync(leftSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!leftResult.IsSuccess || leftResult.Value is null)
        {
            return FromResult(leftResult, correlationId, "Failed to load left session.");
        }

        OperationResult<ReplayDecodeProjection> rightResult = await _sessions
            .GetProjectionAsync(rightSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!rightResult.IsSuccess || rightResult.Value is null)
        {
            return FromResult(rightResult, correlationId, "Failed to load right session.");
        }

        ReplayDecodeProjection leftProjection = leftResult.Value;
        ReplayDecodeProjection rightProjection = rightResult.Value;

        SourceArtifactId leftArtifactId = leftProjection.DecodeRun.SourceArtifactId;
        SourceArtifactId rightArtifactId = rightProjection.DecodeRun.SourceArtifactId;

        IReadOnlyList<TelemetryEvent> leftEvents = ConvertToTelemetryEvents(
            leftProjection.Events,
            leftProjection.DecodeRun.DecoderId,
            leftArtifactId);
        IReadOnlyList<TelemetryEvent> rightEvents = ConvertToTelemetryEvents(
            rightProjection.Events,
            rightProjection.DecodeRun.DecoderId,
            rightArtifactId);

        OperationResult<TelemetryComparison> comparisonResult = await _comparator.CompareAsync(
            leftArtifactId,
            leftEvents,
            rightArtifactId,
            rightEvents,
            ComparisonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        if (!comparisonResult.IsSuccess || comparisonResult.Value is null)
        {
            return FromResult(comparisonResult, correlationId, "Comparison failed.");
        }

        OperationResult<TelemetryComparison> saved = await _comparisons
            .AddAsync(comparisonResult.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!saved.IsSuccess || saved.Value is null)
        {
            return FromResult(saved, correlationId, "Comparison created but could not be persisted.");
        }

        var data = new
        {
            comparisonRunId = saved.Value.Run.Id.ToString(),
            saved.Value.Run.ComparatorId,
            saved.Value.Run.ComparatorVersion,
            leftSessionId = leftGuid.ToString("D"),
            rightSessionId = rightGuid.ToString("D"),
            summary = saved.Value.Summary,
        };

        return Success(
            data,
            $"Created comparison run {saved.Value.Run.Id}.",
            correlationId);
    }

    /// <summary>
    /// Converts <see cref="CanonicalEvent"/> records decoded from a replay
    /// into <see cref="TelemetryEvent"/> records that the comparator can
    /// process. The <paramref name="decoderId"/> and
    /// <paramref name="sourceArtifactId"/> preserve provenance chain.
    /// </summary>
    private static TelemetryEvent[] ConvertToTelemetryEvents(
        IReadOnlyList<CanonicalEvent> canonicalEvents,
        string decoderId,
        SourceArtifactId sourceArtifactId)
    {
        TelemetryProvenance provenance = new(
            TelemetrySourceKind.Replay,
            decoderId,
            sourceArtifactId,
            null,
            null);

        TelemetryEvent[] result = new TelemetryEvent[canonicalEvents.Count];
        for (int i = 0; i < canonicalEvents.Count; i++)
        {
            CanonicalEvent ce = canonicalEvents[i];
            result[i] = new TelemetryEvent(
                ce.Sequence,
                null, // SourceTimeUtc — not available in canonical replay events
                ce.ReplayTime,
                ce.Kind.ToString(),
                ce.ParticipantId?.ToString(),
                ce.EntityId,
                ce.ValuesJson,
                provenance);
        }

        return result;
    }

    /// <summary>
    /// Dispatches export sub-commands. Supported: <c>sessions</c> (events as
    /// structured JSON), <c>positions</c> (position samples as structured JSON).
    /// </summary>
    private async ValueTask<CliExecution> ExportAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count == 0)
        {
            return Invalid(
                "cli.export.subcommand_required",
                "Usage: export sessions <battle-session-id> | export positions <battle-session-id>.",
                correlationId);
        }

        string subCommand = invocation.Positionals[0];
        return subCommand switch
        {
            "sessions" => await ExportSessionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            "positions" => await ExportPositionsAsync(invocation, correlationId, cancellationToken).ConfigureAwait(false),
            _ => Invalid(
                "cli.export.unknown_subcommand",
                $"Unknown export subcommand '{subCommand}'. Available: sessions, positions.",
                correlationId),
        };
    }

    /// <summary>
    /// Exports all decoded events for a battle session as structured JSON
    /// with sequence, kind, replay time, participant/entity IDs, and values.
    /// </summary>
    private async ValueTask<CliExecution> ExportSessionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.export.sessions.id_required",
                "export sessions requires one battle-session GUID.",
                correlationId);
        }

        OperationResult<ReplayDecodeProjection> result = await _sessions
            .GetProjectionAsync(new BattleSessionId(value), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return FromResult(result, correlationId, "Export failed.");
        }

        var data = result.Value.Events
            .Select(static e => new
            {
                sequence = e.Sequence,
                kind = e.Kind,
                replayTimeMs = e.ReplayTime.TotalMilliseconds,
                participantId = e.ParticipantId,
                entityId = e.EntityId,
                values = e.ValuesJson,
            })
            .ToList();

        return Success(
            new { sessionId = value.ToString("D"), count = data.Count, events = data },
            $"Exported {data.Count} event(s).",
            correlationId,
            result.Warnings);
    }

    /// <summary>
    /// Exports all position samples for a battle session as structured JSON
    /// with sequence, replay time, participant/entity IDs, raw coordinates,
    /// and coordinate space.
    /// </summary>
    private async ValueTask<CliExecution> ExportPositionsAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 2 ||
            !Guid.TryParse(invocation.Positionals[1], out Guid value))
        {
            return Invalid(
                "cli.export.positions.id_required",
                "export positions requires one battle-session GUID.",
                correlationId);
        }

        OperationResult<ReplayDecodeProjection> result = await _sessions
            .GetProjectionAsync(new BattleSessionId(value), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return FromResult(result, correlationId, "Export failed.");
        }

        var data = result.Value.Positions
            .Select(static p => new
            {
                sequence = p.Sequence,
                replayTimeMs = p.ReplayTime.TotalMilliseconds,
                participantId = p.ParticipantId,
                entityId = p.EntityId,
                rawX = p.RawX,
                rawY = p.RawY,
                rawZ = p.RawZ,
                coordinateSpace = p.RawCoordinateSpace,
            })
            .ToList();

        return Success(
            new { sessionId = value.ToString("D"), count = data.Count, positions = data },
            $"Exported {data.Count} position(s).",
            correlationId,
            result.Warnings);
    }

    /// <summary>
    /// Monitors a directory for new <c>.wotbreplay</c> files and auto-imports
    /// each one. Uses <see cref="FileSystemWatcher"/> as a low-latency hint
    /// and periodic directory enumeration as source of truth, matching the
    /// pattern used by <c>BlitzReplayLogMonitor</c>.
    /// </summary>
    /// <remarks>
    /// <para>Existing files in the directory are imported on startup
    /// (idempotent). Each new file gets a 2-second stability delay before
    /// import to allow the writer to finish flushing.</para>
    /// <para>Press Ctrl+C to stop watching. The command returns a summary
    /// with the directory, elapsed time, and import/error counts.</para>
    /// </remarks>
    private async ValueTask<CliExecution> WatchAsync(
        CliInvocation invocation,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (invocation.Positionals.Count != 1)
        {
            return Invalid(
                "cli.watch.directory_required",
                "watch requires exactly one directory path.",
                correlationId);
        }

        string directory = Path.GetFullPath(invocation.Positionals[0]);
        if (!Directory.Exists(directory))
        {
            return Invalid(
                "cli.watch.directory_missing",
                $"Directory '{directory}' does not exist.",
                correlationId);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Watching {Directory} for new .wotbreplay files…", directory);
        }

        int importedCount = 0;
        int errorCount = 0;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        ConcurrentDictionary<string, bool> processed = new(StringComparer.OrdinalIgnoreCase);

        using FileSystemWatcher watcher = new(directory, "*.wotbreplay")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        TaskCompletionSource<bool> fileDetected = new();
        void OnCreated(object _, FileSystemEventArgs e)
        {
            fileDetected.TrySetResult(true);
        }

        void OnError(object _, ErrorEventArgs e)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4200, "WatchError"),
                    "File system watcher error: {ExceptionType}.",
                    e.GetException().GetType().Name);
            }

            fileDetected.TrySetResult(true);
        }

        watcher.Created += OnCreated;
        watcher.Error += OnError;

        // Enumerate files that already exist in the directory (idempotent).
        try
        {
            foreach (string existing in Directory.EnumerateFiles(
                         directory, "*.wotbreplay", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = Path.GetFullPath(existing);
                if (!processed.TryAdd(normalized, true))
                {
                    continue;
                }

                bool ok = await ImportFileAsync(normalized, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    importedCount++;
                    fileDetected.TrySetResult(true);
                }
                else
                {
                    errorCount++;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4203, "InitialEnumerationError"),
                    "Could not enumerate existing files: {ExceptionType}.",
                    exception.GetType().Name);
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task completed = await Task.WhenAny(
                        fileDetected.Task,
                        Task.Delay(Timeout.Infinite, linked.Token))
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Drain the completion source.
                fileDetected = new TaskCompletionSource<bool>();

                // Scan for new files.
                try
                {
                    foreach (string candidate in Directory.EnumerateFiles(
                                 directory, "*.wotbreplay", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string normalized = Path.GetFullPath(candidate);
                        if (!processed.TryAdd(normalized, true))
                        {
                            continue;
                        }

                        bool ok = await ImportFileAsync(normalized, cancellationToken)
                            .ConfigureAwait(false);
                        if (ok)
                        {
                            importedCount++;
                        }
                        else
                        {
                            errorCount++;
                        }
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    return Failure(
                        CliExitCode.InternalFailure,
                        "cli.watch.directory_removed",
                        "The watched directory was removed while watching.",
                        data: null,
                        correlationId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher.Created -= OnCreated;
            watcher.Error -= OnError;
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - startedAt;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            string elapsedString = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            _logger.LogInformation(
                "Watched {Directory} for {Elapsed}. Imported {ImportedCount}, {ErrorCount} error(s).",
                directory,
                elapsedString,
                importedCount,
                errorCount);
        }

        return Success(
            new
            {
                directory,
                elapsed = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                importedCount,
                errorCount,
            },
            $"Imported {importedCount} replay(s) with {errorCount} error(s) in {elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}.",
            correlationId);
    }

    /// <summary>
    /// Imports one replay file after a 2-second stability delay. Returns
    /// <see langword="true"/> on success, <see langword="false"/> on failure
    /// (the caller is responsible for counting). Does not throw on import
    /// failures — those are logged and counted as errors.
    /// </summary>
    private async ValueTask<bool> ImportFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // Wait for the file to stabilise (the writer may still be flushing).
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        string fileName = Path.GetFileName(path);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Importing {FileName}…", fileName);
        }

        try
        {
            OperationResult<ReplayIngestionOutcome> result = await _ingestion.ImportAsync(
                new ReplayIngestionRequest(
                    path,
                    "application/vnd.wotblitz.replay",
                    ".wotbreplay",
                    MaximumArtifactBytes: 128 * 1024 * 1024,
                    DecoderLimits.Default),
                cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    ReplayIngestionOutcome outcome = result.Value!;
                    _logger.LogInformation(
                        "Imported {FileName} → artifact {ArtifactId}, decode run {DecodeRunId}.",
                        fileName,
                        outcome.Artifact.Id,
                        outcome.DecodeRun.DecodeRun.Id);
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    ApplicationError? error = result.Error;
                    _logger.LogWarning(
                        new EventId(4201, "ImportFailed"),
                        "Failed to import {FileName}: {ErrorCode} — {ErrorMessage}.",
                        fileName,
                        error?.Code,
                        error?.Message);
                }
            }

            return result.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    new EventId(4202, "ImportException"),
                    "Exception importing {FileName}: {ExceptionType} — {Message}.",
                    fileName,
                    exception.GetType().Name,
                    exception.Message);
            }

            return false;
        }
    }

    /// <summary>
    /// Parses an integer option with bounds checking. Returns
    /// <see langword="false"/> when the value is present but unparseable
    /// or out of range; the caller is responsible for producing the
    /// appropriate error envelope.
    /// </summary>
    private static bool TryGetInteger(
        IReadOnlyDictionary<string, string?> options,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        out int result)
    {
        if (!options.TryGetValue(key, out string? raw))
        {
            result = defaultValue;
            return true;
        }

        return int.TryParse(raw, out result) && result >= minimum && result <= maximum;
    }

    /// <summary>
    /// Maps a successful or failed <see cref="OperationResult{T}"/> into a
    /// <see cref="CliExecution"/>. Failures are routed through
    /// <see cref="MapExitCode"/> for stable exit-code classification.
    /// </summary>
    private static CliExecution FromResult<T>(
        OperationResult<T> result,
        Guid correlationId,
        string successMessage)
    {
        if (result.IsSuccess)
        {
            return Success(result.Value, successMessage, correlationId, result.Warnings);
        }

        ApplicationError error = result.Error ??
            new ApplicationError("internal.unknown", "An unknown application error occurred.");
        return Failure(
            MapExitCode(error.Code),
            error.Code,
            error.Message,
            data: null,
            correlationId,
            error.Retryable,
            result.Warnings);
    }

    /// <summary>Builds a successful CLI execution envelope.</summary>
    private static CliExecution Success(
        object? data,
        string message,
        Guid correlationId,
        IReadOnlyList<string>? warnings = null) =>
        new(
            CliExitCode.Success,
            new CliEnvelope(
                "1",
                Success: true,
                correlationId,
                data,
                warnings ?? [],
                Errors: []),
            message);

    /// <summary>Builds a failure envelope for argument-validation errors.</summary>
    private static CliExecution Invalid(
        string code,
        string message,
        Guid correlationId,
        CliExitCode exitCode = CliExitCode.InvalidArguments) =>
        Failure(exitCode, code, message, data: null, correlationId);

    /// <summary>
    /// Builds a failure envelope for commands that are reserved but not
    /// yet implemented in this milestone.
    /// </summary>
    private static CliExecution Unsupported(string command, Guid correlationId) =>
        Failure(
            CliExitCode.UnsupportedCapability,
            $"cli.{command}.not_available",
            $"The '{command}' command is reserved but not available in this milestone.",
            data: null,
            correlationId);

    /// <summary>Builds a general failure CLI execution envelope.</summary>
    private static CliExecution Failure(
        CliExitCode exitCode,
        string code,
        string message,
        object? data,
        Guid correlationId,
        bool retryable = false,
        IReadOnlyList<string>? warnings = null) =>
        new(
            exitCode,
            new CliEnvelope(
                "1",
                Success: false,
                correlationId,
                data,
                warnings ?? [],
                [new CliError(code, message, retryable)]),
            message);

    /// <summary>
    /// Maps stable application error codes to CLI exit codes by keyword
    /// matching, so the envelope stays deterministic even when a new
    /// error code is added.
    /// </summary>
    private static CliExitCode MapExitCode(string errorCode)
    {
        if (errorCode.Contains("cancelled", StringComparison.Ordinal))
        {
            return CliExitCode.Cancelled;
        }

        if (errorCode.Contains("unsupported", StringComparison.Ordinal))
        {
            return CliExitCode.UnsupportedCapability;
        }

        if (errorCode.Contains("busy", StringComparison.Ordinal) ||
            errorCode.Contains("conflict", StringComparison.Ordinal) ||
            errorCode.Contains("already_exists", StringComparison.Ordinal))
        {
            return CliExitCode.ConflictOrBusy;
        }

        if (errorCode.Contains("invalid", StringComparison.Ordinal) ||
            errorCode.Contains("not_found", StringComparison.Ordinal) ||
            errorCode.Contains("malformed", StringComparison.Ordinal))
        {
            return CliExitCode.InvalidInput;
        }

        return CliExitCode.InternalFailure;
    }
}
