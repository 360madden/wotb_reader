#Requires -Version 5.1
<#
.SYNOPSIS
  OD-048 monitor-and-correlate session driver (strategy v4, M1 + M2): stage
  candidate addresses from the decoded replay trajectory, monitor them while
  the replay plays, and correlate each address's value series against the
  replay's known trajectory. M2 family mapping: mid-battle, the driver
  re-stages the +/-16-byte neighbors of the top provisional survivors so the
  final correlate maps the sibling x/y/z components in the same session.

.DESCRIPTION
  This is the replay-guided correlation layer that replaces the exact-pause
  scan (OD-047 M1). No precise pause is required: the replay plays at 1x, the
  driver re-reads a fixed staged address set every -ReadIntervalSeconds, and
  the host scorer (TrajectoryCorrelationScorer) matches each address's value
  series against the decoded replay's per-entity trajectories with a
  time-shift sweep, per axis, with sign flips. The winning evidence is an
  address that reproduces the movement sequence with direction/speed changes.

  Shift audit: survivors whose winning shift rides the sweep EDGE (within 2s
  of -MaxTimeShiftSeconds) are demoted from strong to suspect
  (verdict evidence-edge-aligned) and listed under suspectEdgeAligned -- a
  boundary-aligned shift means the true alignment is probably beyond the
  sweep (bad anchor or load latency exceeded the bound).

  Family mapping (M2): after -FamilyRefineAfterRounds rounds, a provisional
  correlate picks the top survivors (score >= -FamilyMinScore, cap
  -FamilySurvivorCap; edge-aligned candidates are NOT rejected here -- short
  series have wide ambiguity bands that ride the sweep edges, and the final
  correlate re-audits alignment); their +/-16-byte neighbors are added to the
  staged set, so the remaining rounds record the sibling components. A pass
  that finds zero survivors DEFERS to a later round (cadence
  -FamilyRefineRetryGapRounds) instead of self-terminating, so M2 is not
  permanently disabled by one short-series pass. The final correlate's
  families section groups the scored addresses into coordinate families
  (same entity, one byte window); a family whose three members reproduce
  x/y/z at distinct offsets (none edge-aligned) upgrades the verdict to
  family-complete -- one session mapped the whole coordinate vector.

  Staging: the driver fetches the decoded session trajectory (viewpoint
  entity first, then the most-moving entities), waits -StageDelaySeconds for
  the battle to load after the Start marker, then scans the game process for
  Float values near the ground-truth sample nearest the expected current
  replay tick (one scan per axis). The tolerance is auto-scaled from the
  entity's maximum speed x the load-latency bound, so the band covers the
  battle entity's live position regardless of load jitter. The union is the
  staged set; the scan retries until it finds candidates (battle loaded).

  Viewpoint-first pivot (-StageViewpointOnly): stage only the viewpoint
  player, skip family refinement (no XYZ neighbor assembly), and restrict
  the correlate results + families to the viewpoint entity - the server
  scores every address against the BEST-matching entity trajectory, so
  decoy addresses tracking a teammate's movement would otherwise surface as
  alternate-entity matches. The auto-trace then arms the first strong
  viewpoint survivor via the solo path: one discriminating live coordinate,
  no complete XYZ family, no waiting to assemble siblings.

  Battle-time budget: staging scans are expensive (tens of seconds each), so
  the driver derives a hard staging deadline from the decoded battle duration
  (battle end - 30s minimum monitor window). Staging never runs past the
  deadline -- it stops with whatever it has staged -- and the monitor exits
  early once the decoded battle duration has elapsed, so a slow staging pass
  cannot consume the whole battle and leave the monitor with an empty world.

  No operator input is needed after the replay launches. The battle plays to
  completion; the gate revokes at battle end and the driver stops.

  -AutoWriteTraceOnVerdict (M2 automation, choreography 7): when the final
  correlate produces a usable family (complete x/y/z triple, else one or
  more members clearing the score + band floors -- FRESH14: a lone
  tight-band non-edge survivor is emitted as a single-member solo family,
  since the strongest artifact produced (FRESH12 0x1FC57238) was
  structurally excluded from every family), the driver IMMEDIATELY invokes
  the write-trace in the same process/launch,
  closing the human-reaction gap on the ~30s green window. The engine is
  -TraceEngine: 'csharp' (default) drives the x86 WriteInterceptor helper
  via scripts/invoke-csharp-write-trace.ps1 (arm PAGE_GUARD, capture every
  write while the game keeps running - the x64dbg write-BP route is dead,
  FRESH32/33); 'x64dbg' is the legacy opt-in via x64dbg-write-trace.ps1.
  The write-trace result (exit code, hits, rips) is written to
  -AutoTraceResultPath (default .data\od-048-autotrace-<timestamp>.json);
  the M1 report itself is never re-written. The campaign exit code stays 0
  even when the auto-trace fails (a no-family / stale / paused verdict is
  recorded in the auto-trace report, not treated as an M1 failure).

.EXITCODES
  0  Campaign completed; report written to .data\od-048-<timestamp>.json
  1  Preflight failure (no host/rendezvous, gate never verified)
  2  Staging failure (trajectory unavailable or scan failed)
  3  Monitor failure (read loop aborted before completion)
  4  Correlate failure
  5  Report could not be written
  6  Attach-smoke gate failed (-AttachSmokeOnFirstRound): the x64dbg
     attach/pause/detach round-trip or memory-BP install is red against the
     live game - fix the debugger wiring, then rerun. The correlate + trace
     window was deliberately NOT spent.
#>
[CmdletBinding()]
param(
    # Decoded battle session GUID providing the ground truth. When empty, the
    # driver auto-picks the most recent decoded session from the read API.
    [string]$SessionId = '',
    [int]$WaitVerifiedSeconds = 300,
    # How many entities to stage on: the viewpoint plus the most-moving
    # entities. Each staged entity costs three scans (x/y/z of its first
    # position sample).
    [int]$StageTopN = 3,
    # Viewpoint-first discovery pivot: stage ONLY the viewpoint player's
    # trajectory (no top movers). The pivot's goal is ONE discriminating
    # viewpoint coordinate: correlate results are restricted to the viewpoint
    # entity (alternate-entity decoys excluded), family refinement is skipped
    # (no XYZ neighbor assembly), and the auto-trace fires on the first strong
    # viewpoint survivor via the solo path - no complete-family requirement.
    # Fails closed (exit 2) when the trajectory has no IsViewpoint=true entity.
    [switch]$StageViewpointOnly,
    # FloatTolerance for each staging scan (world units). MUST stay small:
    # live probes show any band >= 0.5 floods 10000 candidates in this game
    # (the coordinate field drowns in address-ordered garbage). 0.001 (exact
    # float match) is the empirically selective setting (~1500 candidates).
    [double]$ScanTolerance = 0.001,
    # Hard cap on the staged union.
    [int]$MaxStaged = 3000,
    # Load-settle delay after the gate verifies (the Start marker fires when
    # loading BEGINS; the battle entities with live positions exist only after
    # LoadGameScene completes). Staging scans run after this delay.
    [int]$StageDelaySeconds = 15,
    # Staging attempts: each retry re-estimates the current replay tick and
    # rescans with a fresh delay, so a battle that is still loading on the
    # first attempt is caught on a later one.
    [int]$MaxStagingAttempts = 3,
    [double]$ReadIntervalSeconds = 2.0,
    # Read rounds; the battle length (duration_ticks / 10MHz) bounds the useful
    # window. Dead Rail is ~271s; default 90 rounds at 2s covers ~180s.
    [int]$MaxReadRounds = 90,
    # Per-axis correlation tolerance (world units).
    [double]$TolerancePerAxis = 6.0,
    # Time-shift sweep bound (seconds); absorbs Start-marker anchor error AND
    # load latency (the battle starts at tick 0 some seconds AFTER the Start
    # marker, so the observed series trails the anchor by the load time).
    # 30s covers observed load latencies; the server cap is 120.
    # FRESH19: FRESH18's z-axis rode the -30s sweep edge at -18.5s with span
    # 275 -- the true shift is BEYOND the old sweep. The 50s attendance
    # estimate is per-replay (this replay took ~20-30s longer in attendance,
    # putting the true match-begin at marker+70..80). The scorer must be able
    # to REACH the true shift instead of pinning the band at the edge, or the
    # band-floor gate refuses everything as edge-aligned. 90s covers an
    # attendance of 50-90s plus signal slack.
    [int]$MaxTimeShiftSeconds = 90,
    # Observed series with a span below this are treated as constants.
    [double]$MinMovingSpan = 0.5,
    # Family refinement (M2): after this many monitor rounds, a provisional
    # correlate picks the top survivors and their +/-FamilyWindowBytes
    # neighbors are added to the staged set for the remaining rounds, so one
    # session can map the sibling x/y/z components without a second launch.
    [int]$FamilyRefineAfterRounds = 10,
    # Provisional-survivor cap for family refinement (8 neighbors each => up
    # to 200 extra staged addresses, comfortably inside the read chunk).
    [int]$FamilySurvivorCap = 25,
    # Byte window around each survivor: three float32s are 12 bytes; 16 leaves
    # headroom for padding or an interleaved field. Neighbor offsets are read
    # at every 4-byte step inside [-Window, +Window] excluding 0.
    [int]$FamilyWindowBytes = 16,
    # Minimum score for a provisional survivor to seed a family.
    [double]$FamilyMinScore = 0.7,
    # Retry cadence for the M2 provisional correlate: when a refinement pass
    # finds zero survivors (short series -> wide ambiguity bands), defer the
    # next attempt this many rounds later so the series accumulate more
    # samples instead of burning a correlate call every round.
    [int]$FamilyRefineRetryGapRounds = 5,
    # Addresses per /discover/read call (server cap is 2000).
    [int]$ReadChunk = 500,
    # Optional wall-clock anchor override (ISO-8601 UTC) captured from the
    # replay Start marker. When empty, the driver anchors at the moment it
    # first observes the verified gate -- correct when the driver starts
    # before the replay reaches battle start; see the WARNING printed on the
    # first poll when the gate is already verified.
    [string]$ReplayStartWallTimeUtc = '',
    # JSON summary output. Default .data\od-048-<timestamp>.json (runtime
    # data, never tracked).
    [string]$ResultPath = '',
    # M2 automation: when the final correlate yields a usable family, invoke
    # the write-trace immediately in the same process (the write-trace's
    # liveness re-read REQUIRES the same game process, and the green window
    # is the battle tail - there is no time for an operator). The auto-trace
    # result report goes to -AutoTraceResultPath. No-op (with a log line)
    # when no family survives.
    [switch]$AutoWriteTraceOnVerdict,
    # Which write-trace engine the auto-trace invokes. 'csharp' (default) is
    # the M2 successor: scripts/invoke-csharp-write-trace.ps1 drives the x86
    # WriteInterceptor helper (tools/WriteInterceptor) - arm PAGE_GUARD on
    # the pages holding the armed member addresses, attach as the process's
    # only debugger, capture every write (RIP/value/registers) while the game
    # KEEPS RUNNING (no breakin, no freeze - the WOW64 attach-freeze class is
    # gone by construction). The x64dbg write-BP route is dead (FRESH32/33
    # root-caused bpm/bph to never fire in this environment), so 'x64dbg'
    # exists only as an explicit opt-in for legacy comparison runs - and the
    # x64dbg attach-smoke gate (-AttachSmokeOnFirstRound) only runs for that
    # engine, because a debugger left attached by the smoke would block the
    # interceptor's DebugActiveProcess.
    [ValidateSet('csharp', 'x64dbg')]
    [string]$TraceEngine = 'csharp',
    # How long the auto-invoked write-trace keeps its window. Budget from the
    # choreography timing table: -MaxReadRounds 70 on Dead Rail leaves ~31s
    # of battle; 25 is the recommended first attempt. The operator may pass a
    # higher value on a later attempt once per-round timing is observed.
    [int]$AutoTraceSeconds = 25,
    # Where the auto-trace evidence report lands. Default
    # .data\od-048-autotrace-<timestamp>.json (runtime data, never tracked).
    [string]$AutoTraceResultPath = '',
    # Minimum correlation score for EVERY member of the family handed to the
    # auto-trace. A below-floor member is noise, not evidence (FRESH10 live:
    # x@0.20 was armed alongside y@1.00 and the trace burned the green window
    # on the noise member, producing family-no-hit). A family that cannot
    # clear the floor is SKIPPED, not armed. 0 disables the floor.
    [double]$AutoTraceMinMemberScore = 0.9,
    # Maximum AMBIGUITY-BAND width (shiftMax - shiftMin, seconds) for every
    # family member handed to the auto-trace. A member whose band covers most
    # of the sweep matches at ANY shift, so its score is cheap regardless of
    # how high it is (FRESH12: FRESH10's armed y@1.00 had a [-10,+30] = 40s
    # band on a 60s sweep -> family-no-hit; 42 of 50 results were degenerate
    # y@~1.0 with 20-60s bands on a 10.9-unit ground axis). A family with a
    # member whose band is missing or wider than the floor is SKIPPED, not
    # armed. 0 disables the floor entirely (unknown bands allowed too, mirror
    # the write-trace). Default 60s = 1/3 of the +-90s (180s) sweep band.
    # FRESH22: this floor was 20s (1/3 of the OLD +-30s sweep) and was never
    # re-derived when the sweep widened to +-90s (commit 888fb58) -- FRESH21
    # refused its real z survivors (span 275, band 31.5s = 17.5% of the sweep)
    # and the trace never fired. The band is the set of shifts achieving the
    # max match count; a band covering most of the sweep = degenerate, a band
    # under ~1/3 of the sweep = discriminating. NOTE: the floor is absolute,
    # not sweep-relative; pair it with the same -MaxTimeShiftSeconds that
    # produced the bands (od-048's default 90 -> 180s sweep).
    [double]$AutoTraceMaxMemberBandSeconds = 60.0,
    # FRESH22: minimum observed movement SPAN (max-min of the value series,
    # game units) for a solo-emitted survivor. The band floor alone cannot
    # catch the degenerate class at the widened sweep (FRESH10's static
    # y@~1.0 had a 20-60s band that now fits): a value that never moves
    # matches a low-information axis at any shift, so its score is cheap and
    # proves nothing about being a written coordinate. A survivor whose span
    # is unknown or below the floor is SKIPPED fail-closed. 0 disables.
    [double]$AutoTraceMinMemberSpan = 10.0,
    # FRESH23: max members emitted in the solo family. The solo path used to
    # emit ONE survivor (the score-max), and FRESH22 proved that can be a
    # partial-window copy (span 75.5 vs the span-275.4 consensus class) that
    # gets zero writes in a live moving window. The real per-frame field is
    # one of MANY synchronized copies, so arm the top-N consensus addresses
    # at once -- the write-trace caps at DR0-DR3 (4). Default 4.
    [int]$AutoTraceMaxSoloMembers = 4,
    # Pass -SkipPlayProbe / -SkipLivenessCheck through to the auto-invoked
    # write-trace (headless validation; the live round keeps both defaults).
    [switch]$AutoTraceSkipPlayProbe,
    [switch]$AutoTraceSkipLivenessCheck,
    # M2 pre-flight gate: after the first monitor round proves the game
    # process is readable, run x64dbg-write-trace.ps1 -AttachSmoke against the
    # LIVE game (hex-pid attach -> pause -> verify -> optional bpm arm/clear
    # -> detach -> verify resume). This validates the two live-only write-trace
    # mechanics mid-battle so a defect is diagnosed as attach-vs-address, not
    # an undiagnosable no-hit run. Fail-closed: a red smoke aborts the
    # campaign (exit 6) before the correlate + trace window is spent.
    [switch]$AttachSmokeOnFirstRound,
    # For -AttachSmokeOnFirstRound: absolute hex address whose guard page is
    # armed+cleared during the smoke (default: the first staged address,
    # which the monitor already reads successfully).
    [string]$AttachSmokeProbeAddress = '',
    # FRESH15e: do not run the attach-smoke until this many seconds of battle
    # have elapsed from the match begin (FRESH35: the REAL match begin from
    # the blitz log when visible, else the anchor+attendance model). The
    # replay's first ~50s are the loading + all-players-in-attendance phase,
    # during which the match is still paused; attaching+pausing the game
    # mid-load froze it (observed 'WoT Blitz (Not Responding)' on the loading
    # screen). The smoke only touches the game once the match has officially
    # begun. A round-count clamp (45) guarantees the gate can never skip the
    # smoke entirely.
    [double]$SmokeMinBattleSeconds = 55.0,
    # FRESH15i: do not run the FIRST STAGING SCAN until this many seconds of
    # battle have elapsed from the match begin (FRESH35: the REAL match begin
    # from the blitz log when visible, else the anchor+attendance model). The
    # replay's first ~50s are loading + all-players-in-attendance (the match
    # is paused, tanks sit at spawn), and the decoded ground truth at tick
    # ~8s is already the tank MOVING - so an exact-match scan at elapsed
    # 8.6s cannot find the live position field (value mismatch) and stages
    # ~1500 decoy floats that happen to hold that value, poisoning the entire
    # series (FRESH15i: staged at elapsed 8.6s -> 31 edge-aligned weak
    # results, 0 strong survivors). Waiting until the match officially begins
    # makes the staged sample value match the live in-battle field, so the
    # true field is staged among the decoys and the correlate can find it.
    # Mirrors the attach-smoke gate.
    [double]$StageMinBattleSeconds = 55.0,
    # FRESH18: the replay-start anchor (Start replay event marker) fires when
    # the replay STARTS LOADING, but the decoded trajectory's tick 0 is the
    # moment the MATCH officially begins - which is this many seconds LATER
    # (loading + all-players-in-attendance; the user-observed value is ~50s,
    # matching the StageMinBattleSeconds / SmokeMinBattleSeconds defaults).
    # The staging scan targets ground-truth tick (elapsed - attendance) and
    # the correlate maps wall->tick from (marker + attendance), so the needed
    # shift is ~0 instead of -50s (unreachable by the +/-30s sweep - the
    # FRESH15j edge-aligned-all-results signature).
    [double]$AttendanceLatencySeconds = 50.0,
    [string]$RepoRoot = '',
    # FRESH30 post-mortem (OD-RECOVERY-051): the decoded-duration battle-end
    # model ran ~3.5 minutes past the REAL battle end (blitz log onLeaveWorld),
    # so the smoke fired 26s after the battle was over and the trace invoked
    # 70s after, into a dead world whose log had gone silent -> the host
    # monitor revoked -> STOP_gate=Denied exit 5 with zero window time. The
    # driver therefore watches the newest blitz log for the real battle-end
    # signature (player onLeaveWorld / OnLeaveBattle) and (a) stops the round
    # loop early with 'battle-ended-log' instead of trusting the model, and
    # (b) BEFORE invoking the auto-trace, re-verifies the gate is still
    # OfflineReplayVerified AND the log shows no battle-end; otherwise it
    # skips the trace with a clean 'battle-ended-skip' verdict (exit 0)
    # instead of burning the window on a guaranteed denial. Fail-closed: the
    # watcher+skip are ON unless this switch is passed (opt-in escape hatch
    # only for offline runs where the blitz log cannot be resolved).
    [switch]$AllowTraceAfterBattleEnd,
    # FRESH35 (FRESH34 post-mortem): the replay plays at ~2x on the
    # accelerated launch (FRESH34: 271.4s decoded in 134s wall), so the
    # decoded-duration battle-end model runs ~2.5 min PAST the real end and
    # the auto-trace fires on the result screen. When the blitz log already
    # shows the real battle end (Get-BlitzRealWindow), the real end wins and
    # this estimate is unused; when the log has not yet shown it (battle
    # still live), the budget predicts realEnd = realMatchBegin + decoded /
    # speed. Default 1.0 keeps the historical behavior; pass the measured
    # speed (~2.0) once a session records playbackSpeed in the report.
    [double]$PlaybackSpeedEstimate = 1.0,
    # FRESH35: wall-clock budget from the fire-by deadline to the moment the
    # interceptor is armed and writing: the final correlate round-trip +
    # verdict + wrapper launch + interceptor attach. The monitor loop stops
    # sampling at (realBattleEnd - TraceStartupSeconds - trace window) so the
    # correlate+verdict+trace all land INSIDE the live battle.
    [double]$TraceStartupSeconds = 20.0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
    else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $dataDir = Join-Path $RepoRoot '.data'
    if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
    # Unique per run: second-resolution timestamps collide on fast-fail
    # reruns (a preflight exit-1 rerun in the same second overwrites the
    # prior report). The campaign suffix keeps retries diagnosable without
    # clobbering evidence.
    $ResultPath = Join-Path $dataDir ("od-048-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-" + ([Guid]::NewGuid().ToString('N').Substring(0, 6)) + ".json")
}

function Write-Od048([string]$Message) {
    Write-Host ("od048: " + $Message)
}

function Get-Rendezvous {
    try {
        $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem $dir -File -ErrorAction Stop |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $file) { return $null }
        $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        # Expiry check (bug-hunt round 18): a host that was HARD-killed leaves
        # the record on disk (graceful shutdown deletes it), and its capability
        # lease is dead. Accepting it makes the driver POST a dead host's token
        # until the host restarts and republishes -- every request 401s with
        # web.local_capability_required and the campaign wastes rounds before
        # the gate-lost path fires. An expired record is treated as absent so
        # Refresh-Rendezvous keeps the LAST KNOWN good record (the host may
        # still be up mid-rotation) and the gate check fails fast.
        if ($record.PSObject.Properties['expiresAtUtc'] -and
            -not [string]::IsNullOrWhiteSpace([string]$record.expiresAtUtc)) {
            try {
                # InvariantCulture (repo convention, cf. Convert-LogTimeToUtc):
                # ISO 8601 parses identically on any machine.
                $expiresAt = [datetime]::Parse(
                    [string]$record.expiresAtUtc,
                    [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
                if ($expiresAt -lt [datetime]::UtcNow.AddSeconds(-30)) {
                    return $null
                }
            }
            catch { }
        }
        return $record
    }
    catch { return $null }
}

# The host rotates its short-lived local capability (5-minute lease) and
# re-publishes the rendezvous record every >=15s. A driver that reads the
# record once at startup holds a token that expires mid-battle (battles run
# ~271s), after which every POST 401s with web.local_capability_required even
# though the gate is still verified. Re-read the record so the capability
# header always carries the host's current lease; keep the last known record
# if the re-read fails (the host is up -- the read itself is best-effort).
function Refresh-Rendezvous {
    param([object]$Current)
    $fresh = Get-Rendezvous
    # Guard the capability access: under StrictMode, property access on a
    # JSON object that lacks the key throws PropertyNotFoundException, which
    # would crash the mid-battle refresh instead of keeping the last record.
    if ($null -ne $fresh -and
        $fresh.PSObject.Properties['capability'] -and
        -not [string]::IsNullOrWhiteSpace([string]$fresh.capability)) {
        return $fresh
    }
    return $Current
}

function Get-GateState {
    param([object]$Rendezvous)
    try {
        if (-not $Rendezvous) { return $null }
        # Explicit 10s timeout: a hung host must fail a poll fast, not hang
        # the driver silently (PS 5.1's default is indefinite).
        return Invoke-RestMethod -Uri ($Rendezvous.baseUri + '/api/v1/game/state') -TimeoutSec 10 -Headers @{
            'X-WotBTreader-Capability' = [string]$Rendezvous.capability
        }
    }
    catch { return $null }
}

function Invoke-Api {
    param(
        [object]$Rendezvous,
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null,
        # Explicit timeout: a staging scan is a full-memory pass that can take
        # tens of seconds, and pwsh 7's Invoke-RestMethod default (100s) would
        # abort it mid-scan; PS 5.1's default (indefinite) hangs forever on a
        # dead host. 300s is generous for the slowest scan while still failing
        # a hung call in finite time.
        [int]$TimeoutSec = 300,
        # FRESH19: the host ROTATES its capability token on every rendezvous
        # publish (PublishAsync -> security.Rotate(), every >=15s), so any
        # driver holding a token older than one publish cycle 401s with
        # web.local_capability_required even though the gate is verified and
        # the host is healthy. FRESH18 lost 3 mid-run rounds to exactly this
        # (holes in the observation series widened every ambiguity band). A
        # 401 is not a failure -- it is a signal to re-read the rendezvous
        # record and retry with the fresh token. Bounded retry, 2s apart, so
        # a genuinely revoked host still fails closed in finite time.
        [int]$CapabilityRetries = 5
    )
    $attempt = 0
    while ($true) {
        $params = @{
            Uri        = $Rendezvous.baseUri + $RelativePath
            Method     = $Method
            TimeoutSec = $TimeoutSec
            Headers    = @{ 'X-WotBTreader-Capability' = [string]$Rendezvous.capability }
        }
        if ($null -ne $Body) {
            $params.ContentType = 'application/json'
            $params.Body = ($Body | ConvertTo-Json -Depth 12 -Compress)
        }
        try {
            return Invoke-RestMethod @params
        }
        catch {
        # Log WHY the call failed (status + short error body): the generic
        # FAILED_* messages alone leave the operator blind between a broken
        # request, a gate-revoked 4xx, and a dead host. Loopback URIs and the
        # small error bodies of 400s are error codes, not sensitive data.
        $status = $null
        $detail = ''
        # PS 5.1 throws WebException (has Response, no StatusCode); pwsh 7
        # throws HttpResponseException (has StatusCode, no Response). Bare
        # property access on a non-matching exception type would throw
        # PropertyNotFoundException under StrictMode, so gate every access
        # on a PSObject.Properties presence check (index access is always
        # StrictMode-safe and returns $null for a missing property).
        if ($_.Exception.PSObject.Properties['Response'] -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        elseif ($_.Exception.PSObject.Properties['StatusCode'] -and $_.Exception.StatusCode) {
            $status = [int]$_.Exception.StatusCode
        }
        # ErrorDetails may be null (e.g. connection refused has no response
        # body) -- null.Message throws under StrictMode, so guard the member.
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace([string]$_.ErrorDetails.Message)) {
            $detail = ([string]$_.ErrorDetails.Message -replace '[\r\n]+', ' ').Trim()
            if ($detail.Length -gt 200) { $detail = $detail.Substring(0, 200) }
        }
        # PS 5.1 fallback: WebException keeps the response body in the stream,
        # not in ErrorDetails -- read it so a 400 reason is never invisible.
        if (-not $detail -and $null -ne $_.Exception -and $_.Exception.PSObject.Properties['Response']) {
            try {
                $response = $_.Exception.Response
                if ($null -ne $response -and $response.PSObject.Properties['GetResponseStream']) {
                    $stream = $response.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = New-Object System.IO.StreamReader($stream)
                        $body = $reader.ReadToEnd()
                        $reader.Dispose()
                        $detail = ($body -replace '[\r\n]+', ' ').Trim()
                        if ($detail.Length -gt 200) { $detail = $detail.Substring(0, 200) }
                    }
                }
            }
            catch {
                # Body read failure is not fatal; keep whatever detail we have.
            }
        }
        # FRESH19: a 401 is the capability-rotation race, not a revoked host.
        # Re-read the rendezvous record (the publisher has written the next
        # rotated token) and retry. Only 401s retry; anything else -- gate
        # denial, dead host, malformed request -- fails through immediately.
        if ($status -eq 401 -and $attempt -lt $CapabilityRetries) {
            $fresh = Get-Rendezvous
            if ($null -ne $fresh -and $fresh.PSObject.Properties['capability'] -and
                -not [string]::IsNullOrWhiteSpace([string]$fresh.capability)) {
                $Rendezvous = $fresh
            }
            Write-Od048 ('api_capability_retry path={0} attempt={1} fresh={2}' -f $RelativePath, ($attempt + 1), [bool]$fresh)
            $attempt++
            Start-Sleep -Seconds 2
            continue
        }
        $diag = ('api_failed method={0} path={1}' -f $Method, $RelativePath)
        if ($null -ne $status) { $diag += (' status=' + $status) }
        if ($detail) { $diag += (' body=' + $detail) }
        Write-Od048 $diag
        return $null
        }
    }
}

# Float -> little-endian hex for the staging scan.
function Convert-ToFloatHex {
    param([double]$Value)
    $bytes = [BitConverter]::GetBytes([float]$Value)
    return ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Test-FiniteDouble {
    param([double]$Value)
    return -not ([double]::IsNaN($Value) -or [double]::IsInfinity($Value))
}

# Ambiguity-band width (seconds) of a correlate RESULT (not a family member),
# or $null when the band is unknown. Accepts both wire pairs like the family
# side: the correlate response emits shiftMin/MaxSeconds and the audit block
# re-emits them as shiftBandMin/MaxSeconds. Used by the FRESH14 solo-family
# emission to rank strong survivors by band width and gate emission on the
# band floor.
# FRESH35 (FRESH34 post-mortem): derive the REAL battle window from the
# newest blitz log. The M1 budget's anchor+attendance+decoded-duration math
# runs minutes past the real wall-clock battle end on the accelerated
# (~2x) launch replay (FRESH34: real battle 02:31:34 -> 02:33:48 = 134s wall
# for a 271.4s decoded battle), so the auto-trace invoked after the last log
# line wrote on the frozen result screen (40 guard events, 0 hits). Returns:
#   MatchBeginUtc - last 'BattleController::LoadGameScene ends' at/after the
#                   anchor (the real battle start). The FIRST scene load is
#                   the hangar->replay transition, whose teardown (including
#                   the player's onLeaveWorld) must NOT count as battle end;
#                   only lines at/after match begin do.
#   EndUtc        - last definitive end marker (Stop replay / OnLeaveBattle /
#                   player onLeaveWorld at/after match begin); MinValue when
#                   the log just went silent instead.
#   PlaybackSpeed - decodedDuration / (End - MatchBegin) when both known.
# Every timestamp is UTC: the log's leading time field is UTC wall clock
# (the host lifecycle parser reads it with AssumeUniversal). It is stamped
# with the ANCHOR'S calendar date: ParseExact('HH:mm:ss') alone uses today's
# LOCAL date, so during an evening run local date != UTC date and every line
# compares BEFORE the anchor and is skipped - the FRESH34 watcher-silent
# root cause (all 764 lines dated Aug 6 vs the Aug 7 anchor).
# Convert a blitz log line's leading HH:mm:ss (UTC wall clock) to a UTC
# DateTime on the anchor's calendar date. The +1-day promotion handles a
# battle crossing UTC midnight (a line at 00:05 whose anchor is 23:58 the
# prior day). REVIEW FIX (FRESH35): the promotion must only apply to a
# genuine midnight crossing - a line whose time-of-day is within ~6h BEFORE
# the anchor is a PREVIOUS battle (or the pre-anchor hangar teardown), NOT a
# midnight-crossing current battle, and promoting it would fabricate a future
# timestamp that pass 2 would misread as a battle-end marker (wasted session:
# Test-BlitzBattleEnded returns True at round 1). Returns MinValue when the
# line is not part of this battle.
function Convert-BlitzLogLineUtc {
    param(
        [string]$Line,
        [datetime]$Anchor,
        [datetime]$AnchorDate
    )
    if ($Line -notmatch '^(\d{2}:\d{2}:\d{2})') { return [datetime]::MinValue }
    $lineUtc = [datetime]::SpecifyKind(
        [datetime]::ParseExact(
            $AnchorDate.ToString('yyyy-MM-dd ', [Globalization.CultureInfo]::InvariantCulture) + $Matches[1],
            'yyyy-MM-dd HH:mm:ss',
            [Globalization.CultureInfo]::InvariantCulture),
        [DateTimeKind]::Utc)
    if ($lineUtc -lt $Anchor) {
        # Genuine midnight crossing: the line is a FULL DAY of wall time
        # before the anchor in parsed terms, so it is the NEXT day's early
        # hours of THIS battle. A line only a few hours before the anchor is
        # a previous battle or the pre-battle hangar teardown - skip it.
        if (($Anchor - $lineUtc).TotalHours -gt 6) {
            $lineUtc = $lineUtc.AddDays(1)
        }
        else {
            return [datetime]::MinValue
        }
    }
    if ($lineUtc -lt $Anchor) { return [datetime]::MinValue }
    return $lineUtc
}

function Get-BlitzRealWindow {
    [CmdletBinding()]
    param(
        [string]$AnchorUtc = '',
        [double]$DecodedDurationSeconds = 0.0
    )
    $result = [ordered]@{
        MatchBeginUtc = [datetime]::MinValue
        EndUtc        = [datetime]::MinValue
        PlaybackSpeed = $null
        LogStaleUtc   = [datetime]::MinValue
        BattleActivitySeen = $false
    }
    $log = Get-NewestBlitzLog
    if (-not $log) { return $result }
    $anchor = [datetime]::MinValue
    if (-not [string]::IsNullOrWhiteSpace($AnchorUtc)) {
        $anchor = [datetime]::Parse(
            $AnchorUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    }
    try {
        $logItem = Get-Item -LiteralPath $log -ErrorAction SilentlyContinue
        if ($null -ne $logItem) { $result.LogStaleUtc = $logItem.LastWriteTimeUtc }
        $anchorDate = if ($anchor -eq [datetime]::MinValue) { ([datetime]::UtcNow).Date } else { $anchor.Date }
        # Pass 1: find the LAST 'LoadGameScene ends' at/after the anchor. The
        # replay sequence is Start marker -> scene load (hangar->replay
        # transition, whose teardown logs the player's onLeaveWorld) -> the
        # REAL battle scene -> deaths. Only lines at/after the FINAL scene
        # load can be battle-end evidence; taking the last one here lets pass
        # 2 gate every end marker against the real match begin.
        $lines = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue)
        $finalMatchBegin = [datetime]::MinValue
        foreach ($line in $lines) {
            $lineUtc = Convert-BlitzLogLineUtc -Line $line -Anchor $anchor -AnchorDate $anchorDate
            if ($lineUtc -eq [datetime]::MinValue) { continue }
            if ($line -match 'BattleController::LoadGameScene ends') {
                $finalMatchBegin = $lineUtc
            }
        }
        $result.MatchBeginUtc = $finalMatchBegin
        # Pass 2: end markers AT/AFTER the final match begin only. The
        # hangar->replay transition teardown (before the real scene) can never
        # false-positive now: it predates the final LoadGameScene ends.
        if ($finalMatchBegin -ne [datetime]::MinValue) {
            foreach ($line in $lines) {
                $lineUtc = Convert-BlitzLogLineUtc -Line $line -Anchor $anchor -AnchorDate $anchorDate
                if ($lineUtc -eq [datetime]::MinValue -or $lineUtc -lt $finalMatchBegin) { continue }
                if ($line -match 'VehicleGameLogic::onLeaveWorld') {
                    $result.BattleActivitySeen = $true
                }
                if ($line -match 'STOP_REPLAY_LOCAL|Stop replay event|ReplayRecorder::StopRecording') {
                    if ($lineUtc -gt $result.EndUtc) { $result.EndUtc = $lineUtc }
                }
                if ($line -match 'BattleController::OnLeaveBattle') {
                    if ($lineUtc -gt $result.EndUtc) { $result.EndUtc = $lineUtc }
                }
                if ($line -match 'VehicleGameLogic::onLeaveWorld.*isPlayer: 1') {
                    if ($lineUtc -gt $result.EndUtc) { $result.EndUtc = $lineUtc }
                }
            }
        }
        # Playback speed: decoded duration / real wall battle length. The end
        # marker is preferred; when the log went silent instead (FRESH34: no
        # player onLeaveWorld / stop marker - the log just stops at the last
        # death), the log's last write IS the battle-end evidence.
        $endEvidenceUtc = $result.EndUtc
        if ($endEvidenceUtc -eq [datetime]::MinValue -and $result.LogStaleUtc -gt $result.MatchBeginUtc) {
            $endEvidenceUtc = $result.LogStaleUtc
        }
        if ($result.MatchBeginUtc -ne [datetime]::MinValue -and $endEvidenceUtc -ne [datetime]::MinValue) {
            $wallSeconds = ($endEvidenceUtc - $result.MatchBeginUtc).TotalSeconds
            if ($wallSeconds -gt 1.0 -and $DecodedDurationSeconds -gt 0) {
                $result.PlaybackSpeed = $DecodedDurationSeconds / $wallSeconds
            }
        }
    }
    catch { }
    return $result
}

# FRESH30 post-mortem (OD-RECOVERY-051) + FRESH35: find the newest blitz log
# and return $true when the battle has ACTUALLY ended. Evidence-first: a
# definitive end marker from Get-BlitzRealWindow (Stop replay / OnLeaveBattle
# / player onLeaveWorld after match begin), OR the log has gone silent for
# >20s after the battle began (FRESH34: the log stops at the last death with
# NO definitive marker - silence is the end signal). The decoded-duration
# model overestimates the real wall-clock battle end by minutes on the
# rolled/accelerated launch replay, so the log is the only reliable source.
function Test-BlitzBattleEnded {
    [CmdletBinding()]
    param(
        [string]$AnchorUtc = ''
    )
    $win = Get-BlitzRealWindow -AnchorUtc $AnchorUtc
    if ($win.EndUtc -ne [datetime]::MinValue) { return $true }
    # REVIEW FIX (FRESH35): the log-silence signal alone can false-positive on
    # a >20s log-write pause mid-battle (loading stall, writer throttle). Only
    # trust silence once the battle ACTUALLY produced activity (an
    # onLeaveWorld after match begin = deaths/teardown began) AND the log has
    # gone silent >20s since. A pre-combat stall cannot end the battle.
    if ($win.BattleActivitySeen -and $win.MatchBeginUtc -ne [datetime]::MinValue -and $win.LogStaleUtc -ne [datetime]::MinValue) {
        if (([datetime]::UtcNow - $win.LogStaleUtc).TotalSeconds -gt 20.0) { return $true }
    }
    return $false
}

# Newest blitz log under the game's DAVAProject dir, or a test fixture under
# $RepoRoot/.data if the game dir is unavailable (offline validation only).
function Get-NewestBlitzLog {
    $candidates = @()
    try {
        $gameDir = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
        $candidates += @(Get-ChildItem -LiteralPath $gameDir -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue)
    }
    catch { }
    $fixtureDir = Join-Path $RepoRoot '.data\blitz-logs'
    if (Test-Path -LiteralPath $fixtureDir) {
        $candidates += @(Get-ChildItem -LiteralPath $fixtureDir -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue)
    }
    if ($candidates.Count -eq 0) { return $null }
    return ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

function Get-SurvivorBandWidth {
    param([object]$Result)
    $minB = $null
    $maxB = $null
    if ($Result.PSObject.Properties['shiftBandMinSeconds'] -and $null -ne $Result.shiftBandMinSeconds) {
        $minB = [double]$Result.shiftBandMinSeconds
    }
    elseif ($Result.PSObject.Properties['shiftMinSeconds'] -and $null -ne $Result.shiftMinSeconds) {
        $minB = [double]$Result.shiftMinSeconds
    }
    if ($Result.PSObject.Properties['shiftBandMaxSeconds'] -and $null -ne $Result.shiftBandMaxSeconds) {
        $maxB = [double]$Result.shiftBandMaxSeconds
    }
    elseif ($Result.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $Result.shiftMaxSeconds) {
        $maxB = [double]$Result.shiftMaxSeconds
    }
    if ($null -eq $minB -or $null -eq $maxB) { return $null }
    return [double]($maxB - $minB)
}

# True when a family member is NOT edge-aligned, with the property access
# guarded: under StrictMode a missing edgeAligned property would throw, so a
# bare-family or old-host wire shape must not crash the gate loop. A missing
# property is treated as NOT edge-aligned (usable) - this mirrors the
# write-trace's Test-UsableFamily exactly ('-not $m.PSObject.Properties[...]
# -or -not $m.edgeAligned'), so both gates agree on the same wire. The
# caller uses the bare form: if (Test-MemberNotEdgeAligned -Member $m).
function Test-MemberNotEdgeAligned {
    param([object]$Member)
    return -not $Member.PSObject.Properties['edgeAligned'] -or -not $Member.edgeAligned
}

# -- Viewpoint-first filter (pivot) -----------------------------------------
# Restrict correlate results to the viewpoint player's entity. The correlate
# server scores each address against the BEST-matching entity trajectory, so
# an address staged from viewpoint ground truth can come back matched to a
# different entity (a decoy tracking a teammate) - alternate-entity matches
# are noise and must be excluded before ANY evidence gate (shift audit, strong
# survivors, solo emission, verdict). entityId is guarded: under StrictMode a
# missing property would throw before the null check runs.
function Select-ViewpointResults {
    param([object[]]$Results, [string]$ViewpointEntityId)
    return @($Results | Where-Object {
        $_.PSObject.Properties['entityId'] -and $null -ne $_.entityId -and
        ([string]$_.entityId) -eq $ViewpointEntityId
    })
}

# True when every member of a family has an address present in the
# viewpoint-result address set (i.e. the family was built exclusively from
# addresses the pivot accepted as viewpoint matches). A server-built family
# may group decoy addresses scored under another entity; in viewpoint-only
# mode such families are excluded. Missing/empty members are rejected
# (fail-closed: unknown is not viewpoint).
function Test-FamilyAllViewpoint {
    param([object]$Family, [hashtable]$ViewpointAddresses)
    # Guarded: under StrictMode a family lacking 'members' throws BEFORE the
    # count check would run - a fail-closed $false must not become a crash.
    if (-not $Family.PSObject.Properties['members']) { return $false }
    $members = @($Family.members)
    if ($members.Count -eq 0) { return $false }
    foreach ($m in $members) {
        if (-not $m.PSObject.Properties['address'] -or $null -eq $m.address) { return $false }
        if (-not $ViewpointAddresses.ContainsKey([string]$m.address)) { return $false }
    }
    return $true
}

# Server cap on correlate observations (matches the endpoint validation); the
# driver keeps the family-neighbor series inside the cap with priority.
$correlateMaxObservations = 2000

# Build the correlate observations array from the monitored series.
function Get-CorrelateObservations {
    param([object]$Series)
    $obs = @()
    foreach ($addressKey in $Series.Keys) {
        $list = [System.Collections.Generic.List[object]]$Series[$addressKey]
        if ($list.Count -lt 2) { continue }
        $obs += @{
            Address = $addressKey
            Samples = @($list | ForEach-Object {
                @{ wallTimeUtc = $_.wallTimeUtc; value = $_.value }
            })
        }
    }
    return $obs
}

function New-CorrelateBody {
    param(
        [object]$Observations,
        [string]$SessionId,
        [string]$ReplayStartWallTimeUtc,
        [double]$TolerancePerAxis,
        [int]$MaxTimeShiftSeconds,
        [double]$MinMovingSpan
    )
    return @{
        groundTruthSessionId   = $SessionId
        replayStartWallTimeUtc = $ReplayStartWallTimeUtc
        tolerancePerAxis       = $TolerancePerAxis
        maxTimeShiftSeconds    = $MaxTimeShiftSeconds
        minMovingSpan          = $MinMovingSpan
        observations           = $Observations
    }
}

# Neighbor addresses at every 4-byte step inside +/-WindowBytes (excluding the
# survivor itself): a 16-byte window yields -16,-12,-8,-4,+4,+8,+12,+16.
function Get-FamilyNeighborAddresses {
    param([string]$Address, [int]$WindowBytes, [int]$ValueSize = 4)
    $hex = $Address
    if ($hex.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) { $hex = $hex.Substring(2) }
    $value = [long]::Parse($hex, [Globalization.NumberStyles]::HexNumber, [Globalization.CultureInfo]::InvariantCulture)
    $neighbors = @()
    for ($offset = -$WindowBytes; $offset -le $WindowBytes; $offset += $ValueSize) {
        if ($offset -eq 0) { continue }
        $neighborValue = $value + $offset
        if ($neighborValue -le 0) { continue }
        $neighbors += ('0x{0:X}' -f $neighborValue)
    }
    return $neighbors
}

# -- Preflight: rendezvous + verified gate --
Write-Od048 'preflight_start'
$rendezvous = Get-Rendezvous
if (-not $rendezvous) {
    Write-Od048 'FAILED_no_rendezvous'
    exit 1
}

Write-Od048 'waiting_for_verified_gate'
$deadline = (Get-Date).AddSeconds($WaitVerifiedSeconds)
$state = $null
$replayStartWallUtc = $ReplayStartWallTimeUtc
$pollCount = 0
while ((Get-Date) -lt $deadline) {
    $pollCount += 1
    $state = Get-GateState -Rendezvous $rendezvous
    if ($state -and $state.verificationState -eq 'OfflineReplayVerified') {
        Write-Od048 'gate=OfflineReplayVerified'
        if ([string]::IsNullOrWhiteSpace($replayStartWallUtc)) {
            $replayStartWallUtc = ([DateTime]::UtcNow).ToString('o')
        }
        if ($pollCount -eq 1) {
            Write-Od048 'WARNING anchor_captured_after_verified - if the battle was already underway when this driver started, the wall anchor is wrong and the run will find no evidence. Restart the driver BEFORE the replay reaches battle start, or pass -ReplayStartWallTimeUtc from the Start marker.'
        }
        break
    }
    $vs = if ($state) { [string]$state.verificationState } else { 'no-host' }
    Write-Od048 ("waiting gate=" + $vs)
    Start-Sleep -Seconds 2
}
if ($null -eq $state -or $state.verificationState -ne 'OfflineReplayVerified') {
    Write-Od048 'FAILED_gate_never_verified'
    exit 1
}

Write-Od048 ("staging_delay_s=" + $StageDelaySeconds)
if ($StageDelaySeconds -gt 0) { Start-Sleep -Seconds $StageDelaySeconds }

# -- Ground truth --
$battleSessionId = $SessionId
if ([string]::IsNullOrWhiteSpace($battleSessionId)) {
    # The read API route is /api/v1/sessions (the old /api/v1/read/sessions
    # prefix 404'd -- 2026-08-05 live-round fix), and the wire field is
    # session.battleSessionId (NOT session.id). The launch flow imports the
    # replay into the HOST's data root (the OD launch host defaults to
    # %LocalAppData%\WotBTreader, not .data), so the newest session in THIS
    # host is the just-imported battle -- auto-pick must use the host's own
    # list, never a hardcoded id from another data root.
    $page = Invoke-Api -Rendezvous $rendezvous -Method 'Get' -RelativePath '/api/v1/sessions?limit=50'
    if ($null -eq $page -or $page.items.Count -eq 0) {
        Write-Od048 'FAILED_no_decoded_session'
        exit 2
    }
    # items[].session is nullable on the wire: a decode run with no battle
    # session serializes a null entry. Guard it so StrictMode fails with a
    # clean diagnostic instead of crashing on a member access of null.
    $newestSession = $page.items[0]
    if ($null -eq $newestSession -or $null -eq $newestSession.session) {
        Write-Od048 'FAILED_newest_session_null'
        exit 2
    }
    $battleSessionId = [string]$newestSession.session.battleSessionId
    Write-Od048 ("auto_picked_session=" + $battleSessionId)
}
Write-Od048 ("ground_truth_session=" + $battleSessionId)

$trajectory = Invoke-Api -Rendezvous $rendezvous -Method 'Get' -RelativePath ('/api/v1/game/discover/trajectory/' + $battleSessionId)
if ($null -eq $trajectory -or $trajectory.entities.Count -eq 0) {
    Write-Od048 'FAILED_trajectory_unavailable'
    exit 2
}
Write-Od048 ("duration_ticks=" + $trajectory.durationTicks)

# -- Staging: viewpoint first, then most-moving entities --
$scored = @()
foreach ($entity in $trajectory.entities) {
    $minX = [double]::MaxValue; $maxX = [double]::MinValue
    $minY = [double]::MaxValue; $maxY = [double]::MinValue
    $minZ = [double]::MaxValue; $maxZ = [double]::MinValue
    $maxSpeed = 0.0
    $prevSample = $null
    foreach ($sample in $entity.samples) {
        if ($sample.x -lt $minX) { $minX = $sample.x }
        if ($sample.x -gt $maxX) { $maxX = $sample.x }
        if ($sample.y -lt $minY) { $minY = $sample.y }
        if ($sample.y -gt $maxY) { $maxY = $sample.y }
        if ($sample.z -lt $minZ) { $minZ = $sample.z }
        if ($sample.z -gt $maxZ) { $maxZ = $sample.z }
        if ($null -ne $prevSample) {
            $dtTicks = [double]$sample.replayTimeTicks - [double]$prevSample.replayTimeTicks
            if ($dtTicks -gt 0) {
                $dx = [double]$sample.x - [double]$prevSample.x
                $dy = [double]$sample.y - [double]$prevSample.y
                $dz = [double]$sample.z - [double]$prevSample.z
                $dist = [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
                $speed = $dist / ($dtTicks / 10000000.0)
                if ($speed -gt $maxSpeed) { $maxSpeed = $speed }
            }
        }
        $prevSample = $sample
    }
    $movement = ($maxX - $minX) + ($maxY - $minY) + ($maxZ - $minZ)
    $scored += [pscustomobject]@{
        EntityId    = $entity.entityId
        TankName    = $entity.tankName
        IsViewpoint = $entity.isViewpoint
        Movement    = $movement
        MaxSpeed    = $maxSpeed
        Samples     = $entity.samples
    }
}

$maxSpeedGlobal = 0.0
if ($scored.Count -gt 0) {
    $speedMax = $scored | Measure-Object -Property MaxSpeed -Maximum
    if ($null -ne $speedMax -and $null -ne $speedMax.Maximum) {
        $maxSpeedGlobal = [double]$speedMax.Maximum
    }
}
# The scan band must match the entity's LIVE position value. Empirically the
# game holds many floats in ANY coordinate band: a live probe on the 11.19.0
# client showed tolerance 0.5 already floods 10000 candidates (returned in
# address order, so the true field drowns and never gets staged) while an
# exact match (0.001) yields ~1500. The correlate shift sweep absorbs anchor
# error and load latency at SCORING time; the staging scan must stay selective
# or the staged set is random memory (all constant decoys -> zero results).
$stagingTolerance = [double]$ScanTolerance
Write-Od048 ("max_speed=" + [Math]::Round($maxSpeedGlobal, 2) + " staging_tolerance=" + [Math]::Round($stagingTolerance, 2))

function Select-NearestSample {
    param([object]$Samples, [long]$TargetTick)
    $best = $null
    $bestDistance = [long]::MaxValue
    foreach ($s in $Samples) {
        $distance = [Math]::Abs(([long]$s.replayTimeTicks) - $TargetTick)
        if ($distance -lt $bestDistance) {
            $bestDistance = $distance
            $best = $s
        }
    }
    if ($null -eq $best) { return $Samples[0] }
    return $best
}

# Viewpoint-first pivot: -StageViewpointOnly stages ONLY the viewpoint
# player (no top movers) - the goal is one discriminating viewpoint
# coordinate, so other entities' trajectories are never staged or prioritized.
$viewpointEntity = $scored | Where-Object { $_.IsViewpoint } | Select-Object -First 1
if ($StageViewpointOnly) {
    if ($null -eq $viewpointEntity) {
        Write-Od048 'FAILED_no_viewpoint_entity (trajectory has no IsViewpoint=true entity)'
        exit 2
    }
    $stagingEntities = @($viewpointEntity)
    Write-Od048 ('viewpoint_only stage_entity=' + $viewpointEntity.EntityId + ' tank=' + $viewpointEntity.TankName)
}
else {
    $stagingEntities = @(
        $viewpointEntity
        ($scored | Where-Object { -not $_.IsViewpoint } | Sort-Object Movement -Descending | Select-Object -First ($StageTopN - 1))
    ) | Where-Object { $null -ne $_ }
}
$viewpointEntityId = if ($null -ne $viewpointEntity) { $viewpointEntity.EntityId } else { $null }

if ($stagingEntities.Count -eq 0) {
    Write-Od048 'FAILED_no_staging_entity'
    exit 2
}
$stagingEntities = @($stagingEntities | Select-Object -First $StageTopN)
Write-Od048 ("staging_entities=" + $stagingEntities.Count)

# Stage on the ground-truth sample nearest the expected current replay tick
# (elapsed since the anchor), not the tick-0 sample: the battle entities with
# live positions exist only after load, and by scan time the tank is seconds
# into the battle. The speed-scaled tolerance absorbs the unknown load latency;
# the shift sweep then aligns the observed series to the ground truth.
# Parse the anchor robustly: Z-suffixed, bare-UTC, and explicit-offset ISO
# strings all normalize to UTC (a bare string is ASSUMED UTC, per the
# documented contract, so it must NOT be reinterpreted as local time).
$anchorUtc = [datetime]::Parse(
    $replayStartWallUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
# FRESH18: the decoded trajectory's tick 0 is the MATCH-BEGIN instant, which
# lags the Start marker by the loading+attendance phase. All tick math (staging
# scan target, correlate wall->tick mapping, battle-end budget) must reference
# the match-begin instant, not the marker.
$battleStartUtc = $anchorUtc.AddSeconds([double]$AttendanceLatencySeconds)
# -- Battle-time budget --
# Staging is the expensive step: up to MaxStagingAttempts x StageTopN entities
# x 3 axes = 27 full-memory scans, each taking tens of seconds. Unguarded, a
# slow first attempt plus retries can consume the ENTIRE battle (Dead Rail is
# ~271s; the real decoded sessions average ~250s) and leave the monitor with
# an empty world. Derive a hard staging deadline from the decoded duration:
# staging must never run past (battle end - minimum monitor window). All
# deadline comparisons use UTC explicitly (DateTime comparison in PS ignores
# Kind, so local-vs-UTC mixing would compare wall clocks, not instants).
$durationSeconds = 0.0
if ($null -ne $trajectory.durationTicks -and [double]$trajectory.durationTicks -gt 0) {
    $durationSeconds = [double]$trajectory.durationTicks / 10000000.0
}
$battleEndUtc = $null
if ($durationSeconds -gt 0) { $battleEndUtc = $battleStartUtc.AddSeconds($durationSeconds) }
# FRESH35 (FRESH34 post-mortem): the real battle window from the blitz log
# beats the anchor+attendance+decoded-duration model. FRESH34's model said
# battleEnd 02:36:21 but the real battle ended 02:33:48 (last onLeaveWorld)
# - 2.5 minutes late, so the trace armed on the result screen (40 guard
# events, 0 hits, value frozen at the final position). When the log already
# shows the real match begin AND the real end, use them directly and derive
# the measured playback speed for the report. When only the match begin is
# visible (battle still live), predict the end from the decoded duration /
# -PlaybackSpeedEstimate so the fire-by deadline lands the trace INSIDE the
# live battle.
$realWindow = Get-BlitzRealWindow -AnchorUtc $replayStartWallUtc -DecodedDurationSeconds $durationSeconds
$realMatchBeginUtc = $realWindow.MatchBeginUtc
# Real battle end: the definitive marker when present, else the log-silence
# time (FRESH34: no stop marker - the log just stops at the last death; the
# last write IS the end evidence, and PlaybackSpeed already used it).
$realBattleEndUtc = $realWindow.EndUtc
if ($realBattleEndUtc -eq [datetime]::MinValue -and $realWindow.LogStaleUtc -gt $realMatchBeginUtc) {
    $realBattleEndUtc = $realWindow.LogStaleUtc
}
$measuredPlaybackSpeed = $realWindow.PlaybackSpeed
if ($realMatchBeginUtc -ne [datetime]::MinValue) {
    Write-Od048 ('real_match_begin=' + $realMatchBeginUtc.ToString('o') + ' model_start=' + $battleStartUtc.ToString('o'))
    $battleStartUtc = $realMatchBeginUtc
}
if ($realBattleEndUtc -ne [datetime]::MinValue) {
    Write-Od048 ('real_battle_end=' + $realBattleEndUtc.ToString('o') + ' measured_playback_speed=' + $(if ($null -ne $measuredPlaybackSpeed) { [Math]::Round($measuredPlaybackSpeed, 2) } else { 'n/a' }))
    $battleEndUtc = $realBattleEndUtc
}
elseif ($PlaybackSpeedEstimate -gt 0 -and $null -ne $battleEndUtc -and $realMatchBeginUtc -ne [datetime]::MinValue) {
    # Battle still live: predict the end so the fire-by deadline can stop the
    # monitor in time. (Decoded-duration model at 1x would overshoot by the
    # playback factor; the estimate absorbs it.)
    $battleEndUtc = $realMatchBeginUtc.AddSeconds($durationSeconds / $PlaybackSpeedEstimate)
    Write-Od048 ('battle_end_estimated_speed=' + $PlaybackSpeedEstimate + ' end=' + $battleEndUtc.ToString('o'))
}
$monitorMinSeconds = 30.0
$stagingDeadlineUtc = $null
$monitorExitUtc = $null
$traceFireByUtc = $null
if ($null -ne $battleEndUtc) {
    $stagingDeadlineUtc = $battleEndUtc.AddSeconds(-$monitorMinSeconds)
    # The battle starts at tick 0 some seconds AFTER the Start marker (load
    # latency, absorbed by the shift sweep up to MaxTimeShiftSeconds), so the
    # UPPER bound on wall-time battle end = anchor + duration + max load
    # latency. The monitor must not exit before that bound or it drops the
    # tail observations; the staging deadline may stay at the nominal end
    # (stopping staging early is safe, only losing scan attempts).
    $monitorExitUtc = $battleEndUtc.AddSeconds([double]$MaxTimeShiftSeconds + 10.0)
    # FRESH35 fire-by deadline: stop the monitor sampling window with enough
    # wall time left to run the final correlate + verdict + wrapper launch +
    # interceptor attach (TraceStartupSeconds) AND the trace window itself.
    # The correlate/verdict/trace then all land INSIDE the live battle; the
    # previous behavior ran the trace after the real battle end, writing on
    # the frozen result screen.
    $traceFireByUtc = $battleEndUtc.AddSeconds(-($TraceStartupSeconds + [double]$AutoTraceSeconds))
    Write-Od048 ("battle_duration_s=" + [Math]::Round($durationSeconds, 1) + " staging_deadline=" + $stagingDeadlineUtc.ToString('o') + " fire_by=" + $traceFireByUtc.ToString('o'))
}
# FRESH15i: match-begin gate for the FIRST staging scan (mirror of the
# attach-smoke gate). The Start marker fires when loading BEGINS; the match
# does not officially start until ~50s later (loading + attendance), and the
# decoded ground truth is the tank MOVING at the anchor while the live tank is
# still at spawn. Staging during that window scans against a value the live
# field does not hold yet (it stages decoys, not the position field). Wait
# until battle elapsed >= StageMinBattleSeconds; cap the wait at the staging
# deadline so a short battle cannot have its staging pushed past the end.
# FRESH35: battle-elapsed is measured from the REAL match begin when the
# log has already shown it (battleStartUtc is re-anchored above), not from
# the Start marker - the marker fires when loading BEGINS, the scene loads
# ~seconds later.
$stagingElapsedSec = [Math]::Max(0.0, ([datetime]::UtcNow - $battleStartUtc).TotalSeconds)
if ($stagingElapsedSec -lt $StageMinBattleSeconds) {
    $matchBeginWait = [double]($StageMinBattleSeconds - $stagingElapsedSec)
    if ($null -ne $stagingDeadlineUtc) {
        $toDeadline = ($stagingDeadlineUtc - [datetime]::UtcNow).TotalSeconds
        if ($toDeadline -lt $matchBeginWait) { $matchBeginWait = [Math]::Max(0.0, $toDeadline) }
    }
    if ($matchBeginWait -gt 0) {
        Write-Od048 ("staging_match_begin_gate elapsed_s=" + [Math]::Round($stagingElapsedSec, 1) + " waiting_s=" + [Math]::Round($matchBeginWait, 1) + " min=" + $StageMinBattleSeconds + 's')
        Start-Sleep -Seconds ([int][Math]::Ceiling($matchBeginWait))
    }
}
$stagingStartUtc = [datetime]::UtcNow
$budgetExhausted = $false
$scanFailed = $false
# Edge threshold for the shift-band audit: computed ONCE before staging so the
# mid-battle family refinement and the final survivor audit share the formula.
$edgeThreshold = [Math]::Max(2, $MaxTimeShiftSeconds - 2)
# Family refinement state (M2): provisional-survivor addresses, the staged
# neighbor set, whether the mid-battle pass already ran, and the last round a
# pass was ATTEMPTED (defer cadence; -99 means never attempted).
$familyRefined = $false
$familyRefineRound = 0
$familyRefineLastAttemptRound = -99
$familyRefineAttempts = 0
$familyRefineDeferred = 0
$familySurvivors = @()
# Attach-smoke gate (M2 pre-flight): whether it already ran, its report
# (null until run; a red smoke aborts the campaign with exit 6), and the
# path its JSON report lands at (defaulted when the gate fires).
$attachSmokeDone = $false
$attachSmoke = $null
$smokeResultPath = ''
# FRESH27b: set when the attach-smoke fires on the last sampling round so
# the loop ends immediately after (no samples stamped under the debugger).
$smokeFiredThisRound = $false
# FRESH15e: wall-clock compensation for the attach-smoke's game pause. The
# smoke pauses the game ~10-20s mid-battle; the correlate maps observation
# ticks as (wallTimeUtc - replayStartWallTimeUtc), so without compensation the
# post-smoke samples land ahead of their true tick and the constant-shift
# sweep cannot re-align a mid-series warp (FRESH15e: all 18 viewpoint results
# edge-aligned, max score 0.63 vs 0.9+ in no-pause sessions). The smoke
# reports its exact game-paused window; we subtract it from post-smoke stamps.
$smokePauseCompensationSeconds = 0.0
$familyStaged = [System.Collections.Generic.HashSet[string]]::new()
$familyNeighborAdded = 0
$staged = [System.Collections.Generic.List[string]]::new()
$stagedEntitiesReport = @()
# Per-scan staging budget (breadth fix): the union cap (MaxStaged) is GLOBAL,
# so without per-scan budgets the first scan's candidates (the viewpoint x
# scan alone returns ~1500 exact matches) consume the whole union and every
# later scan -- the y/z axes and all entities after the first -- contributes
# ZERO candidates (FRESH5: staged=3000 yet all 50 results were axis=x; the
# y/z sibling fields were never staged, so no family could form). Reserve a
# slice of the union for the backup entities so every axis of every staged
# entity contributes candidates; the viewpoint scans keep the rest (their
# depth found the true field in FRESH5). The viewpoint's budget is a TOTAL
# across its three axis scans (not per scan), so the first flood (x ~1500)
# cannot eat the reserve -- the union cap is enforced globally regardless.
$reservedForBackup = if ($stagingEntities.Count -le 1) { 0 }
    else { [int]($MaxStaged / [Math]::Max(2, $stagingEntities.Count)) }
$backupScanCount = [Math]::Max(1, ($stagingEntities.Count - 1) * 3)
$perScanBudgetBackup = [Math]::Max(100, [int]($reservedForBackup / $backupScanCount))
# Decremented as the viewpoint's scans add candidates; when exhausted, the
# remaining viewpoint axes are skipped so the reserve is honored.
$viewpointBudgetLeft = $MaxStaged - $reservedForBackup
Write-Od048 ("staging_budget viewpoint_total={0} backup_scan={1} reserved={2}" -f $viewpointBudgetLeft, $perScanBudgetBackup, $reservedForBackup)
$stagingAttempt = 0
while ($stagingAttempt -lt $MaxStagingAttempts -and -not $budgetExhausted) {
    $stagingAttempt += 1
    $staged.Clear()
    $stagedEntitiesReport = @()
    $scanFailed = $false
    $attemptElapsed = ((Get-Date).ToUniversalTime() - $anchorUtc).TotalSeconds
    Write-Od048 ("staging attempt={0} elapsed_s={1}" -f $stagingAttempt, [Math]::Round([Math]::Max(0.0, $attemptElapsed), 1))

    for ($entityIndex = 0; $entityIndex -lt $stagingEntities.Count; $entityIndex++) {
        $entity = $stagingEntities[$entityIndex]
        $entityLogged = $false
        # Viewpoint scans draw from the shared viewpoint total (decremented as
        # candidates are added); backup entities get the fair-share slice so
        # their y/z fields are staged too.
        foreach ($axis in @('x', 'y', 'z')) {
            # Viewpoint total exhausted: skip its remaining axes so the
            # backup reserve is honored (the first flood must not eat it).
            if ($entityIndex -eq 0 -and $viewpointBudgetLeft -le 0) { break }
            # Budget guard: a full-memory scan takes tens of seconds. If the
            # battle is about to end, stop staging and use what we have so the
            # monitor keeps a real observation window.
            if ($null -ne $stagingDeadlineUtc -and ([datetime]::UtcNow -gt $stagingDeadlineUtc)) {
                $budgetExhausted = $true
                Write-Od048 'staging_budget_exhausted'
                break
            }
            # Fresh tick estimate PER AXIS: the estimate computed at attempt
            # start goes stale while the full-memory scans run (tens of
            # seconds each), so the band would trail the tank by scan duration
            # x speed. Recentering before every scan keeps each band on target.
            # FRESH18: tick estimate relative to MATCH-BEGIN (battleStartUtc),
            # not the marker - the decoded trajectory tick 0 is match-begin, so
            # scanning for the value at (elapsed since marker) is ~50s ahead of
            # the live tank (FRESH15j staged at 56s marker-elapsed -> scanned
            # tick 56 while the tank sat at tick ~6, staging decoys).
            $elapsedSeconds = ((Get-Date).ToUniversalTime() - $battleStartUtc).TotalSeconds
            if ($elapsedSeconds -lt 0) { $elapsedSeconds = 0 }
            $stageTickEstimate = [long]($elapsedSeconds * 10000000.0)
            if (-not $entityLogged) {
                Write-Od048 ("staging entity={0} tick_est={1}" -f $entity.EntityId, $stageTickEstimate)
                $entityLogged = $true
            }
            $sample = Select-NearestSample -Samples $entity.Samples -TargetTick $stageTickEstimate
            $rawAxisValue = $sample.$axis
            if ($null -eq $rawAxisValue) { continue }
            $axisValue = [double]$rawAxisValue
            if (-not (Test-FiniteDouble -Value $axisValue)) { continue }
            $scanBody = @{
                FieldName        = ('corr-' + $axis + '-' + [string]$entity.EntityId)
                FieldType        = 'Float'
                ExpectedValueHex = (Convert-ToFloatHex -Value $axisValue)
                FloatTolerance   = $stagingTolerance
                MaxCandidates    = 10000
                MinRegionSize    = 4096
                Alignment        = 1
            }
            $scan = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover' -Body $scanBody
            if ($null -eq $scan -or $null -eq $scan.candidates) {
                $scanFailed = $true
                Write-Od048 ('staging_scan_failed axis=' + $axis + ' (retrying attempt)')
                break
            }
            # Per-scan budget: viewpoint draws from its remaining total (so a
            # 1500-candidate x flood leaves room for y/z and the reserve), and
            # backup scans are capped at their fair share.
            $scanBudget = if ($entityIndex -eq 0) { $viewpointBudgetLeft } else { $perScanBudgetBackup }
            $scanAdded = 0
            foreach ($candidate in $scan.candidates) {
                # Per-scan budget (not just the global cap): a single flooding
                # scan must not starve the remaining axes/entities (see the
                # staging_budget computation above).
                if ($scanAdded -ge $scanBudget -or $staged.Count -ge $MaxStaged) { break }
                # Keep the canonical "0x..." form end-to-end (staging, read
                # batches, series keys, report) so no decimal/hex mismatch can
                # split an address across two identities.
                $hexAddress = [string]$candidate.absoluteAddress
                if ($hexAddress -notmatch '^0x[0-9a-fA-F]+$') { continue }
                if (-not $staged.Contains($hexAddress)) { $staged.Add($hexAddress); $scanAdded += 1 }
            }
            if ($entityIndex -eq 0) { $viewpointBudgetLeft -= $scanAdded }
        }
        # Entity-level budget/scan guard: once exhausted or a scan has failed,
        # stop the entity loop too, so entities whose scans never ran are NOT
        # reported as staged.
        if ($budgetExhausted -or $scanFailed) { break }
        # One report entry per ENTITY (not per axis scan).
        $stagedEntitiesReport += [pscustomobject]@{
            EntityId    = $entity.EntityId
            TankName    = $entity.TankName
            IsViewpoint = $entity.IsViewpoint
        }
    }

    # Break the attempt loop only when we have enough staged addresses or the
    # budget is gone. A scan FAILURE must NOT break here: it falls through to
    # the retry block below so the next attempt (which resets $scanFailed) can
    # succeed -- a failed attempt 1 is retried, not wasted.
    if ($staged.Count -ge 3 -or $budgetExhausted) { break }
    if ($stagingAttempt -lt $MaxStagingAttempts -and -not $budgetExhausted) {
        # Budget-aware retry: never sleep past the staging deadline.
        $retrySleepSeconds = 15
        if ($null -ne $stagingDeadlineUtc) {
            $remainingToDeadline = ($stagingDeadlineUtc - [datetime]::UtcNow).TotalSeconds
            if ($remainingToDeadline -lt 15) { $retrySleepSeconds = [Math]::Max(0, [int]$remainingToDeadline) }
        }
        Write-Od048 ("staging retry in {0}s (battle may still be loading)" -f $retrySleepSeconds)
        Start-Sleep -Seconds $retrySleepSeconds
    }
}
$stagingEndUtc = [datetime]::UtcNow
if ($staged.Count -lt 3) {
    if ($scanFailed) {
        Write-Od048 'FAILED_staging_scan (all attempts failed)'
    }
    else {
        Write-Od048 ("FAILED_staging_too_small staged=" + $staged.Count)
    }
    exit 2
}
Write-Od048 ("staged=" + $staged.Count)
# Snapshot the SCAN-only staged count before the mid-battle family expansion
# so the report's staged.union/capped reflect the scan cap, not the expansion.
$scanStagedCount = $staged.Count

# -- Monitor loop --
$series = [System.Collections.Generic.Dictionary[string, object]]::new()
$round = 0
$readCalls = 0
$readOkSamples = 0
$stoppedReason = 'rounds-exhausted'
while ($round -lt $MaxReadRounds) {
    $round++
    # Refresh the capability BEFORE polling the gate: the host rotates its
    # short-lived lease (~5 min) mid-battle; a stale token 401s every read
    # even while the gate stays verified.
    $rendezvous = Refresh-Rendezvous -Current $rendezvous
    $gate = Get-GateState -Rendezvous $rendezvous
    # PS 5.1 gate-lost diag: the (if ...) expression used to label the gate
    # state is PS7-only syntax and crashes Windows PowerShell at runtime --
    # compute the label before the branch that consumes it.
    if ($null -eq $gate) { $gateDiag = 'no-host' } else { $gateDiag = [string]$gate.verificationState }
    if ($null -eq $gate -or $gate.verificationState -ne 'OfflineReplayVerified') {
        $stoppedReason = 'gate-lost'
        Write-Od048 ("monitor_stop gate=" + $gateDiag)
        break
    }

    # FRESH35 fire-by deadline: stop sampling with enough wall time left for
    # the final correlate + verdict + wrapper launch + trace window to land
    # INSIDE the live battle. REVIEW FIX: the real window is refreshed EVERY
    # round BEFORE the deadline comparison (not only once the initial
    # deadline passes) - with the default -PlaybackSpeedEstimate 1.0 on a
    # ~2x replay the initial estimate is ~2.5 min late, so the old placement
    # only refreshed after the battle had already ended and the trace was a
    # guaranteed 'battle-ended-log' skip. Refreshing first means the deadline
    # snaps to the REAL end the moment deaths appear in the log, whatever the
    # estimate said. The monitor keeps whatever series it has - the correlate
    # still runs on the live samples.
    $rwNow = Get-BlitzRealWindow -AnchorUtc $replayStartWallUtc -DecodedDurationSeconds $durationSeconds
    if ($rwNow.EndUtc -ne [datetime]::MinValue) {
        $battleEndUtc = $rwNow.EndUtc
        $measuredPlaybackSpeed = $rwNow.PlaybackSpeed
        $traceFireByUtc = $battleEndUtc.AddSeconds(-($TraceStartupSeconds + [double]$AutoTraceSeconds))
    }
    if ($null -ne $traceFireByUtc -and ([datetime]::UtcNow -gt $traceFireByUtc)) {
        $stoppedReason = 'fire-by-deadline'
        Write-Od048 ('monitor_stop fire-by-deadline fire_by=' + $traceFireByUtc.ToString('o') + ' real_end=' + $battleEndUtc.ToString('o'))
        break
    }

    # End-of-battle early exit: once the UPPER bound on wall-time battle end
    # (nominal duration + max load latency + trailing window) has elapsed,
    # further rounds only observe an empty world -- stop and correlate what we
    # have instead of burning rounds.
    if ($null -ne $monitorExitUtc -and ([datetime]::UtcNow -gt $monitorExitUtc)) {
        $stoppedReason = 'battle-ended'
        Write-Od048 'monitor_stop battle-ended'
        break
    }

    # FRESH30 post-mortem (OD-RECOVERY-051): the decoded-duration model ran
    # ~3.5 min past the REAL battle end, so rounds 40-49 sampled a dead world
    # and the smoke fired after the battle was over. Watch the blitz log for
    # the real end and stop early -- the correlate still runs on the live
    # samples, and the pre-trace gate recheck (below) skips the trace.
    # FRESH35: refresh the real window (deaths appear mid-battle), so the
    # fire-by deadline snaps to the ACTUAL end the moment the log shows it
    # instead of waiting out the estimate.
    if (-not $AllowTraceAfterBattleEnd -and (Test-BlitzBattleEnded -AnchorUtc $replayStartWallUtc)) {
        $realWindowNow = Get-BlitzRealWindow -AnchorUtc $replayStartWallUtc -DecodedDurationSeconds $durationSeconds
        if ($realWindowNow.EndUtc -ne [datetime]::MinValue -and $null -ne $battleEndUtc) {
            $battleEndUtc = $realWindowNow.EndUtc
            $measuredPlaybackSpeed = $realWindowNow.PlaybackSpeed
            $traceFireByUtc = $battleEndUtc.AddSeconds(-($TraceStartupSeconds + [double]$AutoTraceSeconds))
            Write-Od048 ('real_battle_end_refreshed=' + $battleEndUtc.ToString('o') + ' measured_playback_speed=' + $(if ($null -ne $measuredPlaybackSpeed) { [Math]::Round($measuredPlaybackSpeed, 2) } else { 'n/a' }))
        }
        $stoppedReason = 'battle-ended-log'
        Write-Od048 'monitor_stop battle-ended-log (blitz log shows player left the world)'
        break
    }

    # M2 attach-smoke gate (fail-closed pre-flight): once the game is readable
    # AND the match has officially begun (battle elapsed >= SmokeMinBattleSeconds
    # past the ~50s loading+attendance phase, or a round-count clamp), prove the
    # two live-only write-trace mechanics against the live process - x64dbg
    # hex-pid attach + pause + detach round-trip and (with a probe address)
    # memory-BP install. FRESH15e: attaching+pausing during the loading screen
    # froze the game ('Not Responding'); the smoke must only run in live battle.
    # A red smoke aborts BEFORE the correlate rounds and trace window are
    # spent, so a live defect is diagnosed as attach-vs-address, not an
    # undiagnosable no-hit run.
    # The smoke is x64dbg-ENGINE ONLY: the C# interceptor (TraceEngine
    # 'csharp', the default) attaches via DebugActiveProcess, which fails
    # with ERROR_INVALID_PARAMETER when the process already has a debugger -
    # a smoke that left x32dbg attached (KeepAttached) would block it, and
    # the smoke exists to validate x64dbg-specific attach mechanics the
    # interceptor does not use. Skipped for 'csharp' with a log line.
    if ($TraceEngine -ne 'x64dbg' -and $AttachSmokeOnFirstRound -and -not $attachSmokeDone) {
        Write-Od048 'attach_smoke skipped (TraceEngine=csharp - the interceptor validates its own attach offline; no x64dbg mechanics to pre-flight)'
        $attachSmokeDone = $true
    }
    $smokeElapsedSeconds = [Math]::Max(0.0, ([datetime]::UtcNow - $battleStartUtc).TotalSeconds)
    # FRESH27b: fire the smoke ONLY on the last sampling round, not at round 2.
    # FRESH27 proved that sampling under an attached (kept) x32dbg warps the
    # wall->tick stamps: rounds 3-50 were read under the debugger and the z
    # consensus collapsed from 0.92 (FRESH26, clean sampling) to 0.22 - the
    # correlate could no longer align the series. The smoke's fail-closed job
    # (prove attach/pause/resume BEFORE the trace window is spent) is still
    # satisfied at the last round, and the round's reads are skipped (below)
    # so NOTHING is sampled under the debugger.
    # Keep the FRESH15e attendance guard: never attach during the ~50s
    # loading/attendance phase (attaching+pausing there froze the game).
    # The last-round placement is safe for the default 50 rounds, but a
    # small -MaxReadRounds could otherwise land round N-1 inside attendance.
    $smokeDue = ($round -ge ($MaxReadRounds - 1)) -and ($smokeElapsedSeconds -ge $SmokeMinBattleSeconds)
    # Log the gate only on rounds where the smoke is actually eligible (not
    # every round - the pre-smoke FRESH15e runs spammed this line 40+ times).
    if ($AttachSmokeOnFirstRound -and -not $attachSmokeDone -and $smokeDue) {
        Write-Od048 ('attach_smoke gate elapsed=' + [Math]::Round($smokeElapsedSeconds, 1) + 's min=' + $SmokeMinBattleSeconds + 's round=' + $round + ' readOk=' + $readOkSamples)
    }
    if ($AttachSmokeOnFirstRound -and -not $attachSmokeDone -and $readOkSamples -gt 0 -and $smokeDue -and $TraceEngine -eq 'x64dbg') {
        $smokeScript = Join-Path $PSScriptRoot 'x64dbg-write-trace.ps1'
        if (-not (Test-Path -LiteralPath $smokeScript)) {
            Write-Od048 'attach_smoke FAILED script_missing'
            $stoppedReason = 'attach-smoke-failed'
            break
        }
        $smokeProbe = $AttachSmokeProbeAddress
        if ([string]::IsNullOrWhiteSpace($smokeProbe) -and $series.Count -gt 0) {
            # Default probe: the first address that ACCUMULATED READ SAMPLES
            # this round ($series.Keys) - a guaranteed-live, already-readable
            # game page. NOT $staged[0]: staging only lists candidate
            # addresses; only $readOkSamples > 0 proves SOMETHING read OK, and
            # it need not be $staged[0] (a broken first staged address would
            # make the smoke arm a guard page on an unreadable region and
            # false-red a healthy debugger).
            $smokeProbe = [string]@($series.Keys)[0]
        }
        if ([string]::IsNullOrWhiteSpace($smokeProbe)) {
            Write-Od048 'attach_smoke FAILED no_probe_address (no readable series; game may not be up)'
            $stoppedReason = 'attach-smoke-failed'
            break
        }
        if ([string]::IsNullOrWhiteSpace($smokeResultPath)) {
            $dataDir = Join-Path $RepoRoot '.data'
            if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
            $smokeResultPath = Join-Path $dataDir ("od-048-attach-smoke-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
        }
        $smokeArgs = @{
            AttachSmoke       = $true
            SmokeProbeAddress = $smokeProbe
            SmokeResultPath   = $smokeResultPath
            SkipGateCheck     = $true
            # FRESH26 attach-once: leave the debugger attached + the game
            # resumed so the M2 trace reuses it instead of a second attach
            # (the FRESH25 STOP_gate=Denied root cause). The smoke is the
            # SAFE attach point: battle-start, pause verified, resume verified,
            # relaunchable on failure. The trace then skips its own attach.
            KeepAttached      = $true
        }
        Write-Od048 ('attach_smoke INVOKING round=' + $round + ' probe=' + $smokeProbe)
        try {
            & $smokeScript @smokeArgs
            $smokeExit = $LASTEXITCODE
        }
        catch {
            $smokeExit = -1
            Write-Od048 ('attach_smoke THREW ' + $_.Exception.Message)
        }
        Write-Od048 ('attach_smoke exit=' + $smokeExit)
        # FRESH15e: read the smoke's reported game-paused wall window and
        # compensate all subsequent sample stamps so the mid-series pause does
        # not warp the wall->tick mapping the correlate relies on.
        if ($smokeExit -eq 0 -and (Test-Path -LiteralPath $smokeResultPath)) {
            try {
                $smokeReport = Get-Content -LiteralPath $smokeResultPath -Raw | ConvertFrom-Json
                $pauseStart = [datetime]::MinValue
                $resumeAt = [datetime]::MinValue
                if ($smokeReport.pauseStartUtc) {
                    [datetime]::TryParse([string]$smokeReport.pauseStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$pauseStart) | Out-Null
                }
                if ($smokeReport.resumeUtc) {
                    [datetime]::TryParse([string]$smokeReport.resumeUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$resumeAt) | Out-Null
                }
                if ($resumeAt -gt $pauseStart -and $pauseStart -gt [datetime]::MinValue) {
                    $smokePauseCompensationSeconds = ($resumeAt - $pauseStart).TotalSeconds
                    Write-Od048 ('attach_smoke pause_compensation_s=' + [Math]::Round($smokePauseCompensationSeconds, 2))
                }
            }
            catch {
                Write-Od048 ('attach_smoke compensation_parse_failed: ' + $_.Exception.Message)
            }
        }
        $attachSmokeDone = $true
        # FRESH27b: skip THIS round's reads - the debugger is now attached
        # (keep-attached) and sampling under it warps wall->tick stamps.
        $smokeFiredThisRound = $true
        # FRESH26: whether the smoke left the debugger attached (attach-once
        # handoff). Only true when the smoke report says keptAttached - the
        # trace's -ReuseAttached flag gates on this exact value so the two can
        # never disagree about who owns the debugger.
        $smokeKeptAttached = $false
        if ($smokeExit -eq 0 -and (Test-Path -LiteralPath $smokeResultPath)) {
            try {
                $smokeReport = Get-Content -LiteralPath $smokeResultPath -Raw | ConvertFrom-Json
                $smokeKeptAttached = ($smokeReport.PSObject.Properties['keptAttached'] -and $smokeReport.keptAttached -eq $true)
            }
            catch {
                Write-Od048 ('attach_smoke kept_attached_parse_failed: ' + $_.Exception.Message)
            }
        }
        $attachSmoke = [ordered]@{
            ranUtc        = ([DateTime]::UtcNow).ToString('o')
            atRound       = $round
            probeAddress  = $smokeProbe
            resultPath    = $smokeResultPath
            exitCode      = $smokeExit
            ok            = ($smokeExit -eq 0)
            keptAttached  = $smokeKeptAttached
        }
        Write-Od048 ('attach_smoke keptAttached=' + $smokeKeptAttached)
        if ($smokeExit -ne 0) {
            $stoppedReason = 'attach-smoke-failed'
            Write-Od048 'attach_smoke FAILED aborting before correlate (fix x64dbg attach/memory-BP on the live game, then rerun)'
            Write-Od048 'attach_smoke WARN_game_may_be_left_paused - close the game window if it is frozen'
            break
        }
        # Green smoke: the ~15-20s pause may have aged out the host's
        # lifecycle evidence (gate transiently not verified). Wait for the
        # gate to recover so a green smoke is NOT followed by a gate-lost
        # abort on the next round. The loop's own gate check re-verifies
        # next round anyway - this grace only covers the stall window.
        $gateGraceDeadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $gateGraceDeadline) {
            $rendezvous = Refresh-Rendezvous -Current $rendezvous
            $gate = Get-GateState -Rendezvous $rendezvous
            $gateNow = if ($null -ne $gate) { [string]$gate.verificationState } else { 'no-host' }
            if ($gateNow -eq 'OfflineReplayVerified') { break }
            Write-Od048 ('attach_smoke gate_recovering ' + $gateNow)
            Start-Sleep -Seconds 3
        }
    }

    # FRESH27b: the smoke fired this round (the LAST sampling round) and the
    # debugger is now attached - END the sampling loop so NO samples are ever
    # stamped under the debugger (the FRESH27 wall->tick warp: rounds 3-50
    # read under the kept debugger collapsed the z consensus 0.92 -> 0.22).
    # All rounds 1..N-1 sampled clean; the keep-attach handoff is complete
    # and the trace reuses the debugger after the correlate.
    if ($smokeFiredThisRound) {
        $smokeFiredThisRound = $false
        Write-Od048 ('monitor_stop smoke_fired_last_round rounds=' + $round + ' (all sampling clean, debugger attached for trace)')
        break
    }

    $addressBatch = @()
    $i = 0
    foreach ($address in $staged) {
        $addressBatch += $address
        $i++
        if ($i -ge $ReadChunk) {
            $readCalls += 1
            $readBody = @{
                Addresses = $addressBatch
                ValueKind = 'Float'
                ValueSize = 4
            }
            $read = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/read' -Body $readBody
            if ($null -ne $read -and $null -ne $read.reads) {
                # FRESH15e: subtract the attach-smoke pause from post-smoke
                # stamps so wall->tick stays linear (see $smokePauseCompensationSeconds).
                $stampNow = [DateTime]::UtcNow
                if ($smokePauseCompensationSeconds -gt 0) { $stampNow = $stampNow.AddSeconds(-$smokePauseCompensationSeconds) }
                $wallNow = $stampNow.ToString('o')
                foreach ($item in $read.reads) {
                    if (-not $item.readOk) { continue }
                    $value = [double]::Parse([string]$item.valueSummary, [Globalization.CultureInfo]::InvariantCulture)
                    if (-not (Test-FiniteDouble -Value $value)) { continue }
                    $readOkSamples += 1
                    if ($series.ContainsKey([string]$item.absoluteAddress)) {
                        $list = [System.Collections.Generic.List[object]]$series[[string]$item.absoluteAddress]
                        $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                    }
                    else {
                        $list = [System.Collections.Generic.List[object]]::new()
                        $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                        $series[[string]$item.absoluteAddress] = $list
                    }
                }
            }
            $addressBatch = @()
            $i = 0
        }
    }
    if ($addressBatch.Count -gt 0) {
        $readCalls += 1
        $readBody = @{
            Addresses = $addressBatch
            ValueKind = 'Float'
            ValueSize = 4
        }
        $read = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/read' -Body $readBody
        if ($null -ne $read -and $null -ne $read.reads) {
            $stampNow = [DateTime]::UtcNow
            if ($smokePauseCompensationSeconds -gt 0) { $stampNow = $stampNow.AddSeconds(-$smokePauseCompensationSeconds) }
            $wallNow = $stampNow.ToString('o')
            foreach ($item in $read.reads) {
                if (-not $item.readOk) { continue }
                $value = [double]::Parse([string]$item.valueSummary, [Globalization.CultureInfo]::InvariantCulture)
                if (-not (Test-FiniteDouble -Value $value)) { continue }
                $readOkSamples += 1
                if ($series.ContainsKey([string]$item.absoluteAddress)) {
                    $list = [System.Collections.Generic.List[object]]$series[[string]$item.absoluteAddress]
                    $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                }
                else {
                    $list = [System.Collections.Generic.List[object]]::new()
                    $list.Add([pscustomobject]@{ wallTimeUtc = $wallNow; value = $value })
                    $series[[string]$item.absoluteAddress] = $list
                }
            }
        }
    }

    # Family refinement (M2): once the series carry enough evidence, run a
    # provisional correlate, take the top survivors, and add their
    # +/-FamilyWindowBytes neighbors to the staged set so the remaining rounds
    # capture the sibling x/y/z components. Refinement RETRIES until it finds
    # survivors: a short-series pass at the first eligible round scores few
    # samples over a wide ambiguity band (every candidate rides the sweep edge)
    # and would find zero survivors -- marking the pass done there (FRESH5:
    # 'family_refined round=10 survivors=0' -> families=0) permanently disables
    # M2. So a pass with 0 survivors DEFERS to a later round (cadence
    # -FamilyRefineRetryGapRounds) instead of self-terminating; a transient API
    # failure also defers. The pass marks done only once survivors are found
    # (neighbors staged) or the series are genuinely empty (no scored series).
    # NOTE: the provisional pass deliberately does NOT apply the edge-aligned
    # filter: short series have wide bands that ride the sweep edges, so the
    # filter rejects every candidate (this is what produced survivors=0 at
    # round 10). The provisional pass only SEEDS neighbor staging; the final
    # correlate and the family builder re-audit edge alignment authoritatively.
    # Viewpoint-first pivot: refinement is SKIPPED under -StageViewpointOnly -
    # it assembles XYZ neighbors for a family the pivot never waits on, and
    # each provisional correlate burns a correlate call + series budget that
    # the single final correlate needs for the strongest evidence.
    if (-not $StageViewpointOnly -and -not $familyRefined -and $round -ge $FamilyRefineAfterRounds -and ($round - $familyRefineLastAttemptRound) -ge $FamilyRefineRetryGapRounds) {
        $familyRefineLastAttemptRound = $round
        $familyRefineAttempts += 1
        # Fresh array per attempt: retries must not accumulate stale survivor
        # addresses across passes (only the survivors of THIS pass seed staging).
        $familySurvivors = @()
        $provisionalObs = @(Get-CorrelateObservations -Series $series |
            Sort-Object { $_.Samples.Count } -Descending |
            Select-Object -First $correlateMaxObservations)
        if ($provisionalObs.Count -gt 0) {
            $provisional = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/correlate' -Body (New-CorrelateBody -Observations $provisionalObs -SessionId $battleSessionId -ReplayStartWallTimeUtc $battleStartUtc.ToString('o') -TolerancePerAxis $TolerancePerAxis -MaxTimeShiftSeconds $MaxTimeShiftSeconds -MinMovingSpan $MinMovingSpan)
            if ($null -ne $provisional -and $null -ne $provisional.results) {
                foreach ($r in @($provisional.results)) {
                    if ($familySurvivors.Count -ge $FamilySurvivorCap) { break }
                    if ($null -eq $r.score -or [double]$r.score -lt $FamilyMinScore) { continue }
                    # No edge-aligned rejection here -- see NOTE above.
                    $familySurvivors += [string]$r.address
                }
                foreach ($address in $familySurvivors) {
                    foreach ($neighbor in (Get-FamilyNeighborAddresses -Address $address -WindowBytes $FamilyWindowBytes)) {
                        if ($familyStaged.Contains($neighbor)) { continue }
                        $familyStaged.Add($neighbor) | Out-Null
                        if (-not $staged.Contains($neighbor)) {
                            $staged.Add($neighbor)
                            $familyNeighborAdded += 1
                        }
                    }
                }
                if ($familySurvivors.Count -gt 0) {
                    $familyRefined = $true
                    $familyRefineRound = $round
                    Write-Od048 ("family_refined round={0} survivors={1} neighbors_added={2}" -f $round, $familySurvivors.Count, $familyNeighborAdded)
                }
                else {
                    $familyRefineDeferred += 1
                    Write-Od048 ("family_refine_deferred round={0} survivors=0 (retry after {1} more rounds)" -f $round, $FamilyRefineRetryGapRounds)
                }
            }
        }
    }

    if (($round % 10) -eq 0) {
        Write-Od048 ("round={0}/{1} series={2} samples={3}" -f $round, $MaxReadRounds, $series.Count, $readOkSamples)
    }
    if ($round -lt $MaxReadRounds) {
        Start-Sleep -Milliseconds ([int]($ReadIntervalSeconds * 1000))
    }
}
Write-Od048 ("monitor done rounds={0} reason={1} series={2}" -f $round, $stoppedReason, $series.Count)

# -- Attach-smoke gate: fail-closed abort (exit 6) ----------------------------
# A red pre-flight means the write-trace cannot work against this live game
# (attach, pause, or memory-BP install failed). Correlating would still
# produce a family, and the auto-trace would then burn the green window on an
# undiagnosable no-hit run - so stop now with a diagnostic report instead.
if ($stoppedReason -eq 'attach-smoke-failed') {
    Write-Od048 'attach_smoke FAILED aborting campaign (exit 6) - correlate skipped'
    $failReport = [ordered]@{
        verdict       = 'attach-smoke-failed'
        stoppedReason = $stoppedReason
        monitorRounds = $round
        seriesCount   = $series.Count
        attachSmoke   = $attachSmoke
        ranUtc        = ([DateTime]::UtcNow).ToString('o')
        note          = 'M2 pre-flight gate red - fix the x64dbg attach/memory-BP wiring on the live game, then rerun'
    }
    try {
        $failJson = $failReport | ConvertTo-Json -Depth 8
        [System.IO.File]::WriteAllText($ResultPath, $failJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Od048 ('report_written=' + $ResultPath)
    }
    catch {
        Write-Od048 ('FAILED_report_write: ' + $_.Exception.Message)
    }
    exit 6
}

# -- Correlate --
$observations = @(Get-CorrelateObservations -Series $series)
if ($observations.Count -eq 0) {
    Write-Od048 'FAILED_no_observations_for_correlate'
    exit 3
}

# The correlate endpoint caps observations at 2000 series, but staging can
# yield up to MaxStaged (3000) addresses PLUS the M2 family neighbors. A plain
# most-sampled-first truncation would drop exactly the family-neighbor series
# (they were staged mid-battle and carry fewer samples than the originals), so
# keep every family address first (capped), then fill the remaining budget
# with the most-sampled rest. Record the truncation in the report.
$observationsTotal = $observations.Count
$familyObs = @($observations | Where-Object { $familyStaged.Contains($_.Address) } | Select-Object -First $correlateMaxObservations)
$restObs = @($observations | Where-Object { -not $familyStaged.Contains($_.Address) } | Sort-Object { $_.Samples.Count } -Descending)
$keepRest = $correlateMaxObservations - $familyObs.Count
if ($keepRest -lt 0) { $keepRest = 0 }
$observations = @($familyObs) + @($restObs | Select-Object -First $keepRest)
if ($observationsTotal -gt $observations.Count) {
    Write-Od048 ("correlate observations truncated from {0} to {1} (server cap {2}; family neighbors kept)" -f $observationsTotal, $observations.Count, $correlateMaxObservations)
}

# Refresh the capability one last time: the final correlate can run minutes
# after the last monitor round (post-processing/truncation), by which point a
# 5-minute lease may have rotated.
$rendezvous = Refresh-Rendezvous -Current $rendezvous
$correlated = Invoke-Api -Rendezvous $rendezvous -Method 'Post' -RelativePath '/api/v1/game/discover/correlate' -Body (New-CorrelateBody -Observations $observations -SessionId $battleSessionId -ReplayStartWallTimeUtc $battleStartUtc.ToString('o') -TolerancePerAxis $TolerancePerAxis -MaxTimeShiftSeconds $MaxTimeShiftSeconds -MinMovingSpan $MinMovingSpan)
if ($null -eq $correlated -or $null -eq $correlated.results) {
    Write-Od048 'FAILED_correlate'
    exit 4
}

$results = @($correlated.results)

# Viewpoint-first pivot: restrict the scored results to the viewpoint player's
# entity BEFORE the shift audit, so every downstream gate (strong survivors,
# edge audit, solo emission, verdict) is viewpoint-scoped. The server scores
# each address against the BEST-matching entity trajectory; alternate-entity
# matches are decoys tracking other tanks' movement, not viewpoint evidence.
if ($StageViewpointOnly) {
    $preFilterCount = $results.Count
    # FRESH19 fix: Select-ViewpointResults returns @(...), but PowerShell
    # UNWRAPS the function's pipeline output on return. A SINGLE match
    # becomes a scalar PSCustomObject (whose .Count happens to work via the
    # unified Count property) and ZERO matches become $null -- and
    # $null.Count throws PropertyNotFoundException under StrictMode. The
    # sharper 90s sweep can produce zero viewpoint matches (all addresses
    # scored as alternate-entity decoys), which crashed the campaign after
    # the correlate window was spent (FRESH18's 173-result array masked it).
    # The caller-side @() re-collects the pipeline output into a real array
    # (empty or not), so every downstream .Count is safe.
    $results = @(Select-ViewpointResults -Results $results -ViewpointEntityId ([string]$viewpointEntityId))
    Write-Od048 ('viewpoint_only results=' + $results.Count + '/' + $preFilterCount + ' excluded_non_viewpoint=' + ($preFilterCount - $results.Count))
    if ($results.Count -eq 0) {
        Write-Od048 'viewpoint_only no_viewpoint_results (every match was an alternate-entity decoy)'
    }
}

# Shift audit: a survivor whose winning shift rides the sweep EDGE means the
# true alignment is probably beyond the sweep (anchor wrong or load latency
# exceeded the bound) -- the classic bad-anchor false positive. Demote those
# from "strong" to "suspect" so a broken anchor cannot masquerade as evidence.
# ($edgeThreshold was computed once before staging so the mid-battle family
# refinement and this audit share the same formula.)
$edgeAlignedSurvivors = @()
foreach ($result in $results) {
    $shift = if ($null -eq $result.shiftSeconds) { 0.0 } else { [double]$result.shiftSeconds }
    # Band-based edge detection: the closest-to-zero reported shift can mask an
    # edge-riding alignment by up to (tolerance / local slope) seconds, so flag
    # when EITHER band edge touches the sweep boundary. (Older hosts without
    # the band fields fall back to the reported shift.) The property access is
    # GUARDED: under StrictMode, `$result.shiftMinSeconds` on a PSCustomObject
    # lacking the key throws PropertyNotFoundException BEFORE the null check
    # runs, so the documented fallback would crash instead of falling back.
    $minShift = $shift
    if ($result.PSObject.Properties['shiftMinSeconds'] -and $null -ne $result.shiftMinSeconds) {
        $minShift = [double]$result.shiftMinSeconds
    }
    $maxShift = $shift
    if ($result.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $result.shiftMaxSeconds) {
        $maxShift = [double]$result.shiftMaxSeconds
    }
    $isEdgeAligned = ([Math]::Abs($minShift) -ge $edgeThreshold) -or ([Math]::Abs($maxShift) -ge $edgeThreshold)
    $result | Add-Member -NotePropertyName edgeAligned -NotePropertyValue $isEdgeAligned -Force
    $result | Add-Member -NotePropertyName shiftBandMinSeconds -NotePropertyValue $minShift -Force
    $result | Add-Member -NotePropertyName shiftBandMaxSeconds -NotePropertyValue $maxShift -Force
    if ($isEdgeAligned -and $result.score -ge 0.7) {
        $edgeAlignedSurvivors += $result
    }
}
$strongSurvivors = @($results | Where-Object { $_.score -ge 0.7 -and -not $_.edgeAligned })
$families = @($correlated.families)
# Viewpoint-first pivot: keep only families whose members' addresses belong
# to the viewpoint-scoped results (a server-built family may group decoy
# addresses scored under another entity). The solo family appended below is
# viewpoint-only by construction.
if ($StageViewpointOnly) {
    $vpAddresses = @{}
    foreach ($r in $results) { $vpAddresses[[string]$r.address] = $true }
    $families = @($families | Where-Object { Test-FamilyAllViewpoint -Family $_ -ViewpointAddresses $vpAddresses })
    Write-Od048 ('viewpoint_only families=' + $families.Count)
}
$completeFamilies = @($families | Where-Object { $_.complete })

# FRESH14 solo-survivor arming path: the strongest artifact this pipeline has
# produced (FRESH12: 0x1FC57238, y@1.000, tight INTERIOR band [-10,-7.5] =
# 2.5s, not edge-aligned) was structurally excluded from every family -- its
# +/-16-byte neighbors scored below the family-seed floor, so the builder
# never grouped it and the >=2-member gate could never arm it. When the best
# strong survivor is not already a member of any family, synthesize a
# single-member "solo" family from it (with its real score + ambiguity band)
# so the auto-trace can arm it. Emission is gated on the SAME floors the
# auto-trace applies: the survivor must clear -AutoTraceMinMemberScore,
# -AutoTraceMaxMemberBandSeconds (sweep-derived 60s for +-90) and
# -AutoTraceMinMemberSpan (movement proof) -- a degenerate static y@~1.0 is
# NOT emitted (the FRESH10 armed family's failure class; FRESH22 caught the
# band floor at a stale 20s refusing FRESH21's real span-275 z survivors).
# When nothing clears, the family_mapping_failed message below stands.
$soloFamilyEmitted = $false
if ($strongSurvivors.Count -gt 0) {
    # FRESH23 selection: prefer the FULL-TRAJECTORY consensus class.
    # Tiebreak order: (1) LARGEST movement SPAN -- a field tracking the axis
    # end-to-end carries the axis's full span (FRESH22: the span-275.4 z
    # consensus ~20 copies vs the armed span-75.5 partial that tracked the
    # series only part-way and got ZERO writes in a live moving window); a
    # static value, a partial-window copy, and a low-information y all carry
    # smaller spans; (2) higher score; (3) narrower ambiguity band. Then arm
    # the top-N candidates (DR0-DR3 = 4) so one of the consensus copies
    # catches the per-frame writer.
    $soloCandidates = @()
    $bestSoloBand = [double]::MaxValue
    foreach ($s in $strongSurvivors) {
        $bandW = Get-SurvivorBandWidth -Result $s
        if ($null -eq $bandW) { $bandW = [double]::MaxValue }
        # GUARDED score access, matching the auto-trace gate loop below: under
        # Set-StrictMode a missing score property would throw BEFORE the null
        # check runs, crashing the campaign after the correlate window is
        # spent instead of skipping the survivor (bug-hunt rounds 8/9/14
        # hardened this convention for exactly this reason).
        if (-not $s.PSObject.Properties['score'] -or $null -eq $s.score) { continue }
        # GUARDED axis/sign/shiftSeconds (bug-hunt R2 HIGH): the member synth
        # below reads all three unguarded; under StrictMode a wire shape
        # missing any of them would crash the campaign after the correlate
        # window is spent. Skip such candidates fail-closed instead.
        if (-not $s.PSObject.Properties['axis'] -or $null -eq $s.axis) { continue }
        if (-not $s.PSObject.Properties['sign'] -or $null -eq $s.sign) { continue }
        if (-not $s.PSObject.Properties['shiftSeconds'] -or $null -eq $s.shiftSeconds) { continue }
        if ([double]$s.score -lt $AutoTraceMinMemberScore) { continue }
        if ($AutoTraceMaxMemberBandSeconds -gt 0 -and $bandW -gt $AutoTraceMaxMemberBandSeconds) { continue }
        # FRESH22 span floor: a survivor that never moves (span below the
        # floor) matched a low-information axis at any shift -- its score is
        # cheap and it must not win the trace window. Unknown span is refused
        # fail-closed (a band with no movement proof is not discriminating).
        if ($AutoTraceMinMemberSpan -gt 0) {
            if (-not $s.PSObject.Properties['span'] -or $null -eq $s.span) { continue }
            if ([double]$s.span -lt $AutoTraceMinMemberSpan) { continue }
        }
        $alreadyMember = $false
        foreach ($f in $families) {
            foreach ($m in @($f.members)) {
                if ([string]$m.address -ieq [string]$s.address) { $alreadyMember = $true; break }
            }
            if ($alreadyMember) { break }
        }
        if ($alreadyMember) { continue }
        $soloCandidates += $s
    }
    if ($soloCandidates.Count -gt 0) {
        $soloCandidates = @($soloCandidates | Sort-Object -Property @(
            @{ Expression = { if ($_.PSObject.Properties['span'] -and $null -ne $_.span) { [double]$_.span } else { -1.0 } }; Descending = $true },
            @{ Expression = { [double]$_.score }; Descending = $true },
            @{ Expression = { Get-SurvivorBandWidth -Result $_ }; Descending = $false }
        ))
        if ($soloCandidates.Count -gt $AutoTraceMaxSoloMembers) {
            $soloCandidates = @($soloCandidates[0..($AutoTraceMaxSoloMembers - 1)])
        }
        $bestSolo = $soloCandidates[0]
        $bestSoloBand = Get-SurvivorBandWidth -Result $bestSolo
        if ($null -eq $bestSoloBand) { $bestSoloBand = [double]::MaxValue }
        $soloMembers = @()
        $seenSolo = @{}
        $soloAxes = @{}
        foreach ($sc in $soloCandidates) {
            $addrKey = ([string]$sc.address).ToLowerInvariant()
            if ($seenSolo.ContainsKey($addrKey)) { continue }
            $seenSolo[$addrKey] = $true
            $minB = $null; $maxB = $null
            if ($sc.PSObject.Properties['shiftBandMinSeconds'] -and $null -ne $sc.shiftBandMinSeconds) { $minB = [double]$sc.shiftBandMinSeconds }
            elseif ($sc.PSObject.Properties['shiftMinSeconds'] -and $null -ne $sc.shiftMinSeconds) { $minB = [double]$sc.shiftMinSeconds }
            if ($sc.PSObject.Properties['shiftBandMaxSeconds'] -and $null -ne $sc.shiftBandMaxSeconds) { $maxB = [double]$sc.shiftBandMaxSeconds }
            elseif ($sc.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $sc.shiftMaxSeconds) { $maxB = [double]$sc.shiftMaxSeconds }
            $scSpan = $null
            if ($sc.PSObject.Properties['span'] -and $null -ne $sc.span) { $scSpan = [double]$sc.span }
            $soloMembers += [pscustomobject]@{
                address         = [string]$sc.address
                offsetBytes     = 0
                axis            = $sc.axis
                sign            = $sc.sign
                shiftSeconds    = $sc.shiftSeconds
                shiftMinSeconds = $minB
                shiftMaxSeconds = $maxB
                score           = [double]$sc.score
                edgeAligned     = $false
                # FRESH22: carry the observed movement span so the write-trace's
                # -MinMemberSpan floor can vet it (the degenerate static class).
                span            = $scSpan
            }
            $soloAxes[[string]$sc.axis] = $true
        }
        $soloFamily = [pscustomobject]@{
            baseAddress = [string]$bestSolo.address
            spanBytes   = 0
            axesCovered = @($soloAxes.Keys)
            complete    = $false
            solo        = $true
            members     = @($soloMembers)
        }
        $families = @($families) + @($soloFamily)
        $soloFamilyEmitted = $true
        $bandText = if ($bestSoloBand -lt [double]::MaxValue) { $bestSoloBand.ToString('F1') + 's' } else { 'unknown' }
        # NO address on the log line (bug-hunt R2 HIGH privacy fix): the
        # evidence payload keeps it; stdout carries only axis/score/band/count.
        Write-Od048 ('family_solo_emitted axis=' + $bestSolo.axis + ' members=' + $soloMembers.Count + ' score=' + $bestSolo.score + ' span=' + $(if ($null -eq $bestSolo.span) { 'unknown' } else { $bestSolo.span.ToString('F1') }) + ' band=' + $bandText + ' (was structurally un-armable: not in any family)')
    }
}
# M2 verdict upgrade: a complete family (three components of one entity
# reproduced at distinct offsets, none edge-aligned) is the strongest artifact
# this pipeline produces -- one session mapped the whole coordinate vector.
$verdict = if ($completeFamilies.Count -gt 0) { 'family-complete' }
    elseif ($strongSurvivors.Count -gt 0) { 'evidence-strong' }
    elseif ($edgeAlignedSurvivors.Count -gt 0) { 'evidence-edge-aligned' }
    elseif ($results.Count -gt 0) { 'evidence-mixed' }
    else { 'no-evidence' }

if ($strongSurvivors.Count -gt 0 -and $families.Count -eq 0) {
    Write-Od048 'family_mapping_failed no_families_from_survivors (M2 stop rule: recheck staging before burning another session)'
}

Write-Od048 ("correlate addresses_scored=" + $correlated.addressesScored + " total_samples=" + $correlated.totalSamples)
Write-Od048 ("verdict=" + $verdict + " strong_survivors=" + $strongSurvivors.Count + " families=" + $families.Count + " complete_families=" + $completeFamilies.Count)

# -- Report --
$report = [ordered]@{
    campaign               = 'od-048'
    completedAtUtc         = ([DateTime]::UtcNow).ToString('o')
    battleSessionId        = $battleSessionId
    durationTicks          = $trajectory.durationTicks
    # FRESH18: the correlate anchored wall->tick at battleStartUtc (marker +
    # attendance), so record BOTH the raw marker and the corrected anchor the
    # scoring actually used - the marker alone makes the run look 50s wrong.
    replayStartWallTimeUtc = $replayStartWallUtc
    matchBeginWallTimeUtc  = $battleStartUtc.ToString('o')
    attendanceLatencySeconds = $AttendanceLatencySeconds
    # Viewpoint-first pivot markers (null when the trajectory had no
    # IsViewpoint=true entity, or the switch was off).
    viewpointOnly          = [bool]$StageViewpointOnly
    viewpointEntityId      = if ($null -eq $viewpointEntityId) { $null } else { [string]$viewpointEntityId }
    # M2 pre-flight gate result (null when -AttachSmokeOnFirstRound was off).
    attachSmoke            = $attachSmoke
    staged                 = [ordered]@{
        entities        = $stagedEntitiesReport
        union           = $scanStagedCount
        capped          = ($scanStagedCount -ge $MaxStaged)
        attempts        = $stagingAttempt
        delayS          = $StageDelaySeconds
        tolerance       = $stagingTolerance
        maxSpeed        = $maxSpeedGlobal
        durationS       = [Math]::Round($durationSeconds, 1)
        stagingS        = [Math]::Round(($stagingEndUtc - $stagingStartUtc).TotalSeconds, 1)
        budgetExhausted = $budgetExhausted
        monitorMinS     = $monitorMinSeconds
    }
    monitor                = [ordered]@{
        rounds               = $round
        intervalSeconds      = $ReadIntervalSeconds
        readCalls            = $readCalls
        readOkSamples        = $readOkSamples
        addressesWithSeries  = $series.Count
        stoppedReason        = $stoppedReason
    }
    correlate              = [ordered]@{
        addressesScored       = $correlated.addressesScored
        totalSamples          = $correlated.totalSamples
        observationsSent      = $observations.Count
        observationsTotal     = $observationsTotal
        observationsCap       = $correlateMaxObservations
        familyNeighborsSent   = $familyObs.Count
        shiftAudit            = [ordered]@{
            edgeThresholdSeconds = $edgeThreshold
            edgeAlignedSuspects  = $edgeAlignedSurvivors.Count
            method               = 'band-edges'
        }
        # Axis histogram of the scored results: an all-x (or all-one-axis)
        # report is the M2 family-starvation signature (sibling y/z fields
        # never staged) and must be visible at a glance.
        resultsByAxis         = [ordered]@{
            x = @($results | Where-Object { $_.axis -eq 'x' }).Count
            y = @($results | Where-Object { $_.axis -eq 'y' }).Count
            z = @($results | Where-Object { $_.axis -eq 'z' }).Count
        }
        strongByAxis          = [ordered]@{
            x = @($strongSurvivors | Where-Object { $_.axis -eq 'x' }).Count
            y = @($strongSurvivors | Where-Object { $_.axis -eq 'y' }).Count
            z = @($strongSurvivors | Where-Object { $_.axis -eq 'z' }).Count
        }
    }
    results                = @($results | Select-Object -First 50 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds       = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            edgeAligned        = $_.edgeAligned
            matchCount         = $_.matchCount
            totalSamples  = $_.totalSamples
            span          = $_.span
            score         = $_.score
        }
    })
    strongSurvivors        = @($strongSurvivors | Select-Object -First 20 | ForEach-Object {
        [ordered]@{
            address       = $_.address
            participantId = $_.participantId
            entityId      = $_.entityId
            axis          = $_.axis
            sign          = $_.sign
            shiftSeconds       = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            matchCount         = $_.matchCount
            totalSamples       = $_.totalSamples
            score              = $_.score
        }
    })
    suspectEdgeAligned      = @($edgeAlignedSurvivors | Select-Object -First 20 | ForEach-Object {
        [ordered]@{
            address             = $_.address
            participantId       = $_.participantId
            entityId            = $_.entityId
            axis                = $_.axis
            sign                = $_.sign
            shiftSeconds        = $_.shiftSeconds
            shiftBandMinSeconds = $_.shiftBandMinSeconds
            shiftBandMaxSeconds = $_.shiftBandMaxSeconds
            matchCount          = $_.matchCount
            totalSamples        = $_.totalSamples
            score               = $_.score
        }
    })
    familyRefinement        = [ordered]@{
        refinedAtRound       = $familyRefineRound
        survivorCap          = $FamilySurvivorCap
        windowBytes          = $FamilyWindowBytes
        provisionalSurvivors = $familySurvivors.Count
        neighborsStaged      = $familyNeighborAdded
        attempts             = $familyRefineAttempts
        deferred             = $familyRefineDeferred
        retryGapRounds       = $FamilyRefineRetryGapRounds
        totalStaged          = $staged.Count
    }
    families                = @($families | ForEach-Object {
        [ordered]@{
            baseAddress = $_.baseAddress
            spanBytes   = $_.spanBytes
            axesCovered = @($_.axesCovered)
            complete    = $_.complete
            # FRESH14: true for a synthesized single-member family emitted
            # from a lone tight-band non-edge survivor (structurally excluded
            # from the real families). The write-trace selects it through the
            # same floors as any other family.
            solo        = $(if ($_.PSObject.Properties['solo'] -and $_.solo) { $true } else { $false })
            members     = @($_.members | ForEach-Object {
                [ordered]@{
                    address             = $_.address
                    offsetBytes         = $_.offsetBytes
                    axis                = $_.axis
                    sign                = $_.sign
                    shiftSeconds        = $_.shiftSeconds
                    # Guarded band access: an old host that omits the band
                    # fields must serialize null (the write-trace then treats
                    # the member as band-unknown and refuses it fail-closed),
                    # not crash the report writer under StrictMode.
                    shiftBandMinSeconds = if ($_.PSObject.Properties['shiftMinSeconds'] -and $null -ne $_.shiftMinSeconds) { [double]$_.shiftMinSeconds } else { $null }
                    shiftBandMaxSeconds = if ($_.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $_.shiftMaxSeconds) { [double]$_.shiftMaxSeconds } else { $null }
                    score               = $_.score
                    edgeAligned         = $_.edgeAligned
                    # FRESH22: movement span when the source carried it (solo
                    # members do; server-family members may not).
                    span                = if ($_.PSObject.Properties['span'] -and $null -ne $_.span) { [double]$_.span } else { $null }
                }
            })
        }
    })
    verdict                = $verdict
    # FRESH14: true when a lone tight-band non-edge survivor (structurally
    # excluded from every family) was emitted as a single-member solo family
    # and IS armable by the auto-trace. False when every strong survivor was
    # already in a family or failed the score/band floors.
    soloFamilyEmitted      = $soloFamilyEmitted
    # FRESH35 (FRESH34 post-mortem): the real battle window from the blitz
    # log + the playback speed derived from it. The decoded-duration model
    # overshot the real end by ~2.5 min (FRESH34: real 02:33:48 vs model
    # 02:36:21) because the launch replay plays at ~2x - the measured speed
    # calibrates the next run's -PlaybackSpeedEstimate.
    realMatchBeginUtc      = if ($realMatchBeginUtc -ne [datetime]::MinValue) { $realMatchBeginUtc.ToString('o') } else { $null }
    realBattleEndUtc       = if ($realBattleEndUtc -ne [datetime]::MinValue) { $realBattleEndUtc.ToString('o') } else { $null }
    measuredPlaybackSpeed  = if ($null -ne $measuredPlaybackSpeed) { [Math]::Round($measuredPlaybackSpeed, 2) } else { $null }
    playbackSpeedEstimate  = $PlaybackSpeedEstimate
    traceFireByUtc         = if ($null -ne $traceFireByUtc) { $traceFireByUtc.ToString('o') } else { $null }
}

try {
    $json = $report | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($ResultPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Od048 ("report_written=" + $ResultPath)
}
catch {
    Write-Od048 ('FAILED_report_write: ' + $_.Exception.Message)
    exit 5
}

# -- M2 automation: auto-invoke the write-trace on a surviving family --
# Closes the choreography 7 human-reaction gap: the write-trace must start
# within seconds of the verdict (the green window is the battle tail, ~30s),
# and its liveness re-read (exit 8) REQUIRES the same game process, so a
# manual start loses most of the window. The auto-trace result is a SEPARATE
# report: the M1 report is immutable once written.
$autoTrace = $null
if ($AutoWriteTraceOnVerdict) {
    # Usable-family gate (M2 stop rule): a complete family is the prize; any
    # family with one or more members is worth a trace window -- but only if
    # every member clears the score floor AND at least one member is NOT
    # edge-aligned (a bad-anchor family whose every member rides the sweep
    # edge would burn the trace window on fabricated alignment). FRESH14
    # removed the >=2-member requirement: the strongest artifact the pipeline
    # has produced (FRESH12's 0x1FC57238, tight interior band) was
    # structurally excluded from every family because its +/-16-byte
    # neighbors scored below the seed floor, so the driver now emits a
    # single-member solo family from the best lone tight-band non-edge
    # survivor. Score floor added FRESH11: a below-floor member is noise
    # (FRESH10: x@0.20 armed alongside y@1.00 -> family-no-hit), so a family
    # with any member under -AutoTraceMinMemberScore is skipped, not armed.
    # The complete-family shortcut is GONE: 'complete' only proves 3 axes +
    # no edge alignment, not that every member scored (a noise member inside
    # a 'complete' triple would still burn the window).
    $usableFamily = $null
    $usableSkipReason = ''
    # Best near-miss across ALL rejected families: the skip log reports the
    # CLOSEST family to the floors, not the last one scanned (families arrive
    # member-count-desc, so the most informative near-miss is the highest
    # weakest-member score seen, not whichever family the loop hit last).
    $bestNearMiss = -1.0
    # Band-floor near-miss: the NARROWEST widest-band seen (a family whose
    # widest member band is just over the floor is a better next attempt than
    # one whose members match at every shift).
    $bestNearMissBand = [double]::MaxValue
    # Distinguish a wire-shape regression (score property missing on EVERY
    # member) from a genuinely weak correlate: both fail the floor with
    # weakest_score=0.00, but the first is an API bug, the second is a real
    # evidence verdict. Flag it so a live round is never misread.
    $anyScoreSeen = $false
    # True once ANY family passes the member-count gate. The score-missing
    # diagnosis below must only fire when a family actually existed but
    # carried no scores - otherwise a report with no families at all (no
    # family passes the count gate, so the score loop never runs and
    # $anyScoreSeen stays false) would be misread as a wire-shape regression
    # when the real reason is that no family exists (bug-hunt round 8/9).
    $anyGe1MemberFamily = $false
    # Catch-all reason when every family fails for a reason the near-miss
    # trackers don't cover (e.g. no families - the memberCount guard rejects
    # them before any floor runs). FRESH14: the count gate is >=1 member, so
    # a single-member (solo) family emitted from a lone tight-band survivor is
    # eligible; the >=2 rule was removed because FRESH12's 0x1FC57238 (the
    # strongest artifact produced) was structurally excluded from every family.
    $usableSkipReason = 'no_family_with_members'
    foreach ($f in @($families)) {
        $memberCount = @($f.members).Count
        if ($memberCount -lt 1) { continue }
        $anyGe1MemberFamily = $true
        $weakestScore = [double]::MaxValue
        foreach ($m in @($f.members)) {
            if ($m.PSObject.Properties['score'] -and $null -ne $m.score) {
                $score = [double]$m.score
                $anyScoreSeen = $true
            }
            else {
                $score = 0.0
            }
            if ($score -lt $weakestScore) { $weakestScore = $score }
        }
        if ($weakestScore -lt $AutoTraceMinMemberScore) {
            if ($weakestScore -gt $bestNearMiss) { $bestNearMiss = $weakestScore }
            continue
        }
        # Band floor (FRESH13): a member whose ambiguity band is missing or
        # wider than the floor matches at any shift -- its score is cheap and
        # proves nothing about being a written coordinate. Refuse the family
        # regardless of score. The correlate wire emits shiftMin/MaxSeconds;
        # the M1 report re-emits them as shiftBandMin/MaxSeconds, so accept
        # either pair.
        $widestBand = 0.0
        $bandUnknown = $false
        foreach ($m in @($f.members)) {
            $minB = $null; $maxB = $null
            if ($m.PSObject.Properties['shiftBandMinSeconds'] -and $null -ne $m.shiftBandMinSeconds) { $minB = [double]$m.shiftBandMinSeconds }
            elseif ($m.PSObject.Properties['shiftMinSeconds'] -and $null -ne $m.shiftMinSeconds) { $minB = [double]$m.shiftMinSeconds }
            if ($m.PSObject.Properties['shiftBandMaxSeconds'] -and $null -ne $m.shiftBandMaxSeconds) { $maxB = [double]$m.shiftBandMaxSeconds }
            elseif ($m.PSObject.Properties['shiftMaxSeconds'] -and $null -ne $m.shiftMaxSeconds) { $maxB = [double]$m.shiftMaxSeconds }
            if ($null -eq $minB -or $null -eq $maxB) { $bandUnknown = $true; break }
            $width = $maxB - $minB
            if ($width -gt $widestBand) { $widestBand = $width }
        }
        # 0 disables the floor ENTIRELY (unknown bands allowed), mirroring the
        # write-trace's Test-FamilyBanded - otherwise the two gates disagree
        # on the same report when an operator passes 0 to disable it.
        if ($AutoTraceMaxMemberBandSeconds -le 0) {
            $hasNonEdgeAligned = $false
            foreach ($m in @($f.members)) {
                if (Test-MemberNotEdgeAligned -Member $m) { $hasNonEdgeAligned = $true; break }
            }
            if ($hasNonEdgeAligned) { $usableFamily = $f; $usableSkipReason = ''; break }
            # A family that PASSED the score floor but is all-edge-aligned must
            # NOT update $bestNearMiss (that tracker means "score floor
            # rejected"); update it only on genuine score rejections above.
            $usableSkipReason = ('all_members_edge_aligned weakest_score=' + $weakestScore.ToString('F2'))
            continue
        }
        if ($bandUnknown -or $widestBand -gt $AutoTraceMaxMemberBandSeconds) {
            if (-not $bandUnknown -and $widestBand -lt $bestNearMissBand) { $bestNearMissBand = $widestBand }
            $usableSkipReason = ('degenerate_member_band widest_band=' + $widestBand.ToString('F1') + 's floor=' + $AutoTraceMaxMemberBandSeconds + 's')
            if ($bandUnknown) { $usableSkipReason = 'member_band_unknown (no shiftMin/MaxSeconds on the wire)' }
            continue
        }
        $hasNonEdgeAligned = $false
        foreach ($m in @($f.members)) {
            if (Test-MemberNotEdgeAligned -Member $m) { $hasNonEdgeAligned = $true; break }
        }
        if ($hasNonEdgeAligned) { $usableFamily = $f; $usableSkipReason = ''; break }
        # See the -le 0 branch: an edge-aligned rejection is NOT a score-floor
        # rejection, so it must not feed the score near-miss tracker.
        $usableSkipReason = ('all_members_edge_aligned weakest_score=' + $weakestScore.ToString('F2'))
    }

    if ($null -eq $usableFamily) {
        if (-not $anyScoreSeen -and $anyGe1MemberFamily) {
            $usableSkipReason = 'members_missing_score - check the correlate wire shape (score absent on every family member)'
        }
        elseif ($bestNearMiss -ge 0) {
            $usableSkipReason = ('best_near_miss=' + $bestNearMiss.ToString('F2') + ' below_floor=' + $AutoTraceMinMemberScore)
        }
        elseif ($bestNearMissBand -lt [double]::MaxValue) {
            $usableSkipReason = ('best_near_miss_band=' + $bestNearMissBand.ToString('F1') + 's over_floor=' + $AutoTraceMaxMemberBandSeconds + 's')
        }
        Write-Od048 ('auto_write_trace SKIPPED no_usable_family verdict=' + $verdict + ' reason=' + $usableSkipReason)
    }
    else {
        # TraceEngine selects the write-trace driver. 'csharp' (default) is
        # the M2 successor: invoke-csharp-write-trace.ps1 drives the x86
        # WriteInterceptor helper (tools/WriteInterceptor), which arms
        # PAGE_GUARD and captures writes while the game keeps running - no
        # x64dbg, no attach-freeze, no DR0-DR3 limit. 'x64dbg' is the legacy
        # opt-in (the write-BP route is dead; kept for comparison runs).
        $wtScript = if ($TraceEngine -eq 'x64dbg') { Join-Path $PSScriptRoot 'x64dbg-write-trace.ps1' }
            else { Join-Path $PSScriptRoot 'invoke-csharp-write-trace.ps1' }
        if (-not (Test-Path -LiteralPath $wtScript)) {
            Write-Od048 ('auto_write_trace FAILED script_missing=' + $wtScript)
        }
        else {
            if ([string]::IsNullOrWhiteSpace($AutoTraceResultPath)) {
                $dataDir = Join-Path $RepoRoot '.data'
                if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
                $AutoTraceResultPath = Join-Path $dataDir ("od-048-autotrace-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
            }
            # FRESH21: budget the trace window against the ACTUAL battle tail
            # at invoke time. FRESH20's fixed 25s window straddled battle end
            # (the battle ended 11s into it -> STOP_gate=Denied, exit 5), and
            # the wrapper (re-pre-arm + attach + script inject) costs ~50-60s
            # on top of the correlate. Cap the window to (tail - 15s margin),
            # floored at 10s and ceilinged at the requested AutoTraceSeconds.
            $tailSeconds = if ($null -ne $battleEndUtc) {
                ([DateTime]$battleEndUtc - [DateTime]::UtcNow).TotalSeconds
            }
            else { [double]::MaxValue }
            $traceSeconds = [int][Math]::Min($AutoTraceSeconds, [Math]::Max(10, $tailSeconds - 15))
            if ($traceSeconds -ne $AutoTraceSeconds) {
                Write-Od048 ('auto_write_trace window_adjusted requested={0} tail={1}s -> {2}s' -f $AutoTraceSeconds, [int]$tailSeconds, $traceSeconds)
            }
            # FRESH30 post-mortem (OD-RECOVERY-051): re-verify the gate is
            # still OfflineReplayVerified AND the blitz log shows no battle
            # end BEFORE invoking. FRESH30's trace invoked 70s after the real
            # battle end (decoded-duration model was ~3.5 min late), the host
            # monitor had already revoked, and the first gate poll read Denied
            # -> exit 5 with zero window time. Skipping here preserves the
            # strong M1 verdict and writes a clean reason instead of burning
            # the window on a guaranteed denial.
            $wtSkipped = $false
            $wtSkipReason = ''
            if (-not $AllowTraceAfterBattleEnd) {
                $gateNow = Get-GateState -Rendezvous $rendezvous
                $gateNowDiag = if ($null -eq $gateNow) { 'no-host' } else { [string]$gateNow.verificationState }
                $logEnded = Test-BlitzBattleEnded -AnchorUtc $replayStartWallUtc
                if ($null -eq $gateNow -or $gateNow.verificationState -ne 'OfflineReplayVerified' -or $logEnded) {
                    $wtSkipped = $true
                    $wtSkipReason = ('gate=' + $gateNowDiag + ' log_battle_ended=' + $logEnded)
                    Write-Od048 ('auto_write_trace SKIPPED battle_ended_or_gate_lost ' + $wtSkipReason)
                }
            }
            if (-not $wtSkipped) {
                Write-Od048 ('auto_write_trace INVOKING verdict=' + $verdict + ' family_complete=' + $completeFamilies.Count + ' trace_s=' + $traceSeconds)
            }
            # Hashtable splat, NOT an array: PowerShell array-splatting of
            # '-Name value' pairs misaligns argument binding (a switch in the
            # middle shifts the following value onto the wrong parameter --
            # live proof: -TraceSeconds received the FamilyFile path and the
            # trace THREW instead of arming). Hashtable splat binds by name
            # and is immune to the shift. (Reproduced in a minimal repro:
            # array-splat throws, hashtable-splat binds correctly.)
            $wtArgs = @{
                FamilyFile     = $ResultPath
                AutoWriteTrace = $true
                TraceSeconds   = $traceSeconds
                ResultPath     = $AutoTraceResultPath
                # Keep both gates on the same floors: od-048 skips weak/
                # degenerate families here, and the write-trace re-vets with
                # its own -MinMemberScore/-MaxMemberBandSeconds - pass the
                # same values so the two can never disagree (od-048 approves
                # -> write-trace refuses).
                MinMemberScore      = $AutoTraceMinMemberScore
                MaxMemberBandSeconds = $AutoTraceMaxMemberBandSeconds
                # FRESH22: same span floor on both gates so the solo member
                # vetted here can never be refused there for the same reason.
                MinMemberSpan        = $AutoTraceMinMemberSpan
            }
            # FRESH26 attach-once (x64dbg engine only): when the smoke left
            # its debugger attached, the trace reuses it (skips its own
            # attach) - the second attach was the FRESH25 STOP_gate=Denied
            # root cause. The C# interceptor never accepts a pre-attached
            # debugger (DebugActiveProcess fails when the process already
            # has one), so ReuseAttached is passed only to the x64dbg driver.
            if ($TraceEngine -eq 'x64dbg') {
                $wtArgs.ReuseAttached = $smokeKeptAttached
            }
            if ($wtSkipped) {
                $wtExit = 0
                Write-Od048 ('auto_write_trace skipped (battle-ended/gate-lost) - window not spent')
            }
            else {
                # The skip probes are x64dbg-engine diagnostics (they exercise
                # the UIA attach/play-state mechanics); the C# interceptor has
                # no such mechanics, so they are passed only to x64dbg.
                if ($TraceEngine -eq 'x64dbg') {
                    if ($AutoTraceSkipPlayProbe) { $wtArgs.SkipPlayProbe = $true }
                    if ($AutoTraceSkipLivenessCheck) { $wtArgs.SkipLivenessCheck = $true }
                }
                try {
                    & $wtScript @wtArgs
                    $wtExit = $LASTEXITCODE
                }
                catch {
                    $wtExit = -1
                    Write-Od048 ('auto_write_trace THREW ' + $_.Exception.Message)
                }
                Write-Od048 ('auto_write_trace exit=' + $wtExit)
            }
            $autoTrace = [ordered]@{
                invokedUtc  = ([DateTime]::UtcNow).ToString('o')
                engine      = $TraceEngine
                script      = $wtScript
                familyFile  = $ResultPath
                traceSeconds = $traceSeconds
                requestedSeconds = $AutoTraceSeconds
                resultPath  = $AutoTraceResultPath
                exitCode    = $wtExit
                # 0 clean window; 7 paused (SPACE and rerun); 8 stale family
                # (never relaunch between M1 and M2). M1 is unaffected either
                # way: the verdict below stands.
                # The write-trace re-vets the family with its OWN floors after
                # od-048 approved it, so a refusal is a gate-parity signal. But
                # exit 2 is shared with input failures (file missing, unparse,
                # no families, no armed members) - the stdout line (captured in
                # the campaign log) disambiguates, so the verdict stays neutral.
                verdict     = if ($wtSkipped) { 'battle-ended-skip' }
                    elseif ($wtExit -eq 0) { 'trace-complete' }
                    elseif ($wtExit -eq 2) { 'trace-refused-exit2' }
                    elseif ($wtExit -eq 7) { 'trace-replay-paused' }
                    elseif ($wtExit -eq 8) { 'trace-family-stale' }
                    else { 'trace-failed' }
                skipReason  = $wtSkipReason
            }
            try {
                $atJson = $autoTrace | ConvertTo-Json -Depth 6
                [System.IO.File]::WriteAllText($AutoTraceResultPath, $atJson, (New-Object System.Text.UTF8Encoding($false)))
                Write-Od048 ('auto_trace_report_written=' + $AutoTraceResultPath)
            }
            catch {
                Write-Od048 ('auto_trace_report_write_failed: ' + $_.Exception.Message)
            }
        }
    }
}
else {
    Write-Od048 ('auto_write_trace off (pass -AutoWriteTraceOnVerdict for M2 automation)')
}

Write-Od048 ('done verdict=' + $verdict + ' results=' + $results.Count + ' families=' + $families.Count)
exit 0
