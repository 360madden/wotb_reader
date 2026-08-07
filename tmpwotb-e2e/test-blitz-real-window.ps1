# Offline validation for the FRESH35 real-window derivation
# (Get-BlitzRealWindow / Test-BlitzBattleEnded / Convert-BlitzLogLineUtc)
# against a SELF-CONTAINED synthetic FRESH34-style blitz log:
#   - hangar->replay transition teardown (player onLeaveWorld isPlayer:1 at
#     02:31:30, BEFORE the real scene) must NOT count as battle end
#   - real battle: last LoadGameScene ends 02:31:34 -> deaths until 02:33:48
#   - a definitive stop marker (ReplayRecorder::StopRecording at 02:33:48)
#     that the REAL FRESH34 log does NOT contain - the fixture's EndUtc can
#     only come from the fixture, proving the harness never falls back to the
#     live game log (false-confidence guard).
# Plus unit checks for the bug-hunt fixes:
#   - horizon rejection: a previous-battle line parsing AFTER the anchor
#     (anchor 00:05, line 23:58) must be rejected with a duration-aware
#     horizon, and a genuine midnight crossing (anchor 23:58, line 00:05)
#     must still be promoted
#   - degenerate-speed clamp: a 5s wall window for a 271.4s decoded battle
#     (~54x) must yield PlaybackSpeed = $null, never a nonsense speed
#   - live-transition shape: a log whose mtime ~= now (game actively writing,
#     no match begin yet) must yield MatchBeginUtc = MinValue + null speed so
#     the budget can never read battleEndUtc = now
# The fixture lives ONLY under a temp dir passed via -FixtureRoot (default
# $env:TEMP\od-wt-blitz-win-test), NEVER under .data/blitz-logs, so a live
# od-048 run can never pick it up. Cleaned up in a finally block.
[CmdletBinding()]
param(
    [string]$FixtureRoot = ''
)
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) {
    $FixtureRoot = Join-Path $env:TEMP 'od-wt-blitz-win-test'
}
$RepoRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { Split-Path -Parent $PSScriptRoot } else { 'C:\work\wotb_reader' }
$funcs = Join-Path $RepoRoot 'scripts\od-048-monitor-correlate-session.ps1'
# Extract the functions via PowerShell's own AST parser (robust: a manual
# brace-matcher broke on apostrophes inside comments, e.g. "day's").
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($funcs, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    throw ('parse errors in ' + $funcs + ': ' + ($parseErrors[0].Message))
}
$body = ''
foreach ($name in @('Convert-BlitzLogLineUtc', 'Get-BlitzRealWindow', 'Test-BlitzBattleEnded')) {
    $fn = $null
    foreach ($node in $ast.FindAll({ $true }, $true)) {
        if ($node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name) {
            $fn = $node
            break
        }
    }
    if ($null -eq $fn) { throw "function $name not found" }
    $body += $fn.Extent.Text + "`n"
}
$body += "`n`$RepoRoot = '$RepoRoot'`n"
Invoke-Expression $body
# Dependencies the extracted functions call, defined HERE in the same script
# scope (NOT Global: - a Global definition would be shadowed by an extracted
# definition during function resolution, a false-confidence trap the earlier
# harness fell into). Get-NewestBlitzLog returns the fixture; Write-Od048 is
# a no-op diagnostic sink.
function Get-NewestBlitzLog { return $script:testFixture }
# Diagnostic sink: captures (and genuinely reads) the messages the extracted
# functions emit, so a harness run also surfaces the WARNING blitz_log_no_
# match_begin and blitz_log_window_error diagnostics as real data.
$script:od48Diag = @()
function Write-Od048 {
    param([string]$Message)
    if (-not [string]::IsNullOrEmpty($Message)) {
        $script:od48Diag += $Message
    }
}

$exit = 1
try {
    # --- self-contained fixture (unique name; never .data/blitz-logs) ---
    if (-not (Test-Path -LiteralPath $FixtureRoot)) { New-Item -ItemType Directory -Path $FixtureRoot | Out-Null }
    $fixture = Join-Path $FixtureRoot ('blitz-logs_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    $lines = @(
        '02:31:28 [info] 21:31:28 -5 [replay] Start replay event'
        '02:31:28 [info] 21:31:28 -5 [battle] BattleController::LoadGameScene begins'
        '02:31:29 [info] 21:31:29 -5 [battle] BattleController::LoadGameScene ends'
        '02:31:30 [info] 21:31:30 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549395 isPlayer: 0'
        '02:31:30 [info] 21:31:30 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549401 isPlayer: 1'
        '02:31:33 [info] 21:31:33 -5 [battle] BattleController::LoadGameScene begins'
        '02:31:34 [info] 21:31:34 -5 [battle] BattleController::LoadGameScene ends'
        '02:33:21 [info] 21:33:21 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549400 isPlayer: 0'
        '02:33:22 [info] 21:33:22 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549402 isPlayer: 0'
        '02:33:23 [info] 21:33:23 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549398 isPlayer: 0'
        '02:33:48 [info] 21:33:48 -5 [replay] ReplayRecorder::StopRecording'
    )
    Set-Content -LiteralPath $fixture -Value $lines -Encoding ASCII
    # mtime = last line time (02:33:48 UTC) so the silence/playback math is
    # consistent with a real log.
    $target = [datetime]::Parse('2026-08-07T02:33:48.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    (Get-Item -LiteralPath $fixture).LastWriteTimeUtc = $target
    $script:testFixture = $fixture

    $anchor = '2026-08-07T02:31:30.0000000Z'
    $win = Get-BlitzRealWindow -AnchorUtc $anchor -DecodedDurationSeconds 271.4
    Write-Output ('MATCH_BEGIN=' + $(if ($win.MatchBeginUtc -ne [datetime]::MinValue) { $win.MatchBeginUtc.ToString('o') } else { 'NONE' }))
    Write-Output ('END=' + $(if ($win.EndUtc -ne [datetime]::MinValue) { $win.EndUtc.ToString('o') } else { 'NONE' }))
    Write-Output ('PLAYBACK=' + $(if ($null -ne $win.PlaybackSpeed) { [Math]::Round($win.PlaybackSpeed, 2) } else { 'n/a' }))
    Write-Output ('STALE=' + $(if ($win.LogStaleUtc -ne [datetime]::MinValue) { $win.LogStaleUtc.ToString('o') } else { 'NONE' }))
    Write-Output ('ACTIVITY=' + $win.BattleActivitySeen)
    Write-Output ('ENDED=' + (Test-BlitzBattleEnded -AnchorUtc $anchor))

    $pass = $true
    $expectedBegin = [datetime]::Parse('2026-08-07T02:31:34.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    if ($win.MatchBeginUtc -ne $expectedBegin) {
        Write-Output 'FAIL: match begin != 02:31:34 (last LoadGameScene ends)'
        $pass = $false
    }
    # The stop marker is NOT in the real FRESH34 log: if EndUtc != 02:33:48
    # the harness silently tested the live game log (scope/shadow regression).
    $expectedEnd = [datetime]::Parse('2026-08-07T02:33:48.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    if ($win.EndUtc -ne $expectedEnd) {
        Write-Output ('FAIL: EndUtc != 02:33:48 stop marker - fixture not used? got ' + $(if ($win.EndUtc -ne [datetime]::MinValue) { $win.EndUtc.ToString('o') } else { 'NONE' }))
        $pass = $false
    }
    if ($null -eq $win.PlaybackSpeed -or [Math]::Abs([Math]::Round($win.PlaybackSpeed, 2) - 2.03) -gt 0.1) {
        Write-Output ('FAIL: playback speed ~2.03 expected, got ' + $win.PlaybackSpeed)
        $pass = $false
    }
    # The transition teardown (player onLeaveWorld at 02:31:30) must never set
    # EndUtc: EndUtc must be the stop marker (> match begin).
    if ($win.EndUtc -le $win.MatchBeginUtc) {
        Write-Output 'FAIL: EndUtc <= match begin - the hangar teardown was misread as battle end'
        $pass = $false
    }
    if (-not $win.BattleActivitySeen) {
        Write-Output 'FAIL: battle activity (onLeaveWorld after match begin) not detected'
        $pass = $false
    }
    # Midnight-crossing unit check: a line 6h+ before the anchor IS promoted.
    $crossing = Convert-BlitzLogLineUtc -Line '00:05:00 [info] -5 [battle] VehicleGameLogic::onLeaveWorld id: 1 isPlayer: 0' -Anchor ([datetime]::Parse('2026-08-07T23:58:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) -AnchorDate ([datetime]::Parse('2026-08-07T23:58:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)).Date)
    if ($crossing -ne [datetime]::Parse('2026-08-08T00:05:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) {
        Write-Output ('FAIL: midnight crossing not promoted correctly, got ' + $crossing.ToString('o'))
        $pass = $false
    }
    # Horizon rejection (the one-way-heuristic bug): a previous-day line that
    # PARSES AFTER the anchor (anchor 00:05, line 23:58) is 23.8h away - with
    # a duration-aware horizon it must be rejected, not accepted.
    $anchor005 = [datetime]::Parse('2026-08-07T00:05:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    $horizon = $anchor005.AddSeconds(271.4 * 4.0 + 900.0)
    $staleAfter = Convert-BlitzLogLineUtc -Line '23:58:00 [info] -5 [battle] VehicleGameLogic::onLeaveWorld id: 1 isPlayer: 0' -Anchor $anchor005 -AnchorDate $anchor005.Date -HorizonUtc $horizon
    if ($staleAfter -ne [datetime]::MinValue) {
        Write-Output ('FAIL: previous-day line 23:58 not rejected by horizon, got ' + $staleAfter.ToString('o'))
        $pass = $false
    }
    # Pre-anchor line (previous battle same day) is skipped by the 6h rule.
    $preAnchor = Convert-BlitzLogLineUtc -Line '20:00:00 [info] -5 [battle] VehicleGameLogic::onLeaveWorld id: 1 isPlayer: 0' -Anchor ([datetime]::Parse('2026-08-07T21:00:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) -AnchorDate ([datetime]::Parse('2026-08-07T21:00:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)).Date)
    if ($preAnchor -ne [datetime]::MinValue) {
        Write-Output ('FAIL: pre-anchor line (previous battle) not skipped, got ' + $preAnchor.ToString('o'))
        $pass = $false
    }
    # Degenerate-speed clamp: scene end 02:31:34 + one death at 02:31:39 with
    # mtime 02:31:39 -> wall 5s -> ~54x -> PlaybackSpeed must be null, never a
    # nonsense speed that would drive a wrong fire-by deadline.
    $fixture2 = Join-Path $FixtureRoot ('blitz-logs_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    Set-Content -LiteralPath $fixture2 -Value @(
        '02:31:30 [info] 21:31:30 -5 [replay] Start replay event'
        '02:31:34 [info] 21:31:34 -5 [battle] BattleController::LoadGameScene ends'
        '02:31:39 [info] 21:31:39 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549395 isPlayer: 0'
    ) -Encoding ASCII
    (Get-Item -LiteralPath $fixture2).LastWriteTimeUtc = [datetime]::Parse('2026-08-07T02:31:39.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    $script:testFixture = $fixture2
    $win2 = Get-BlitzRealWindow -AnchorUtc $anchor -DecodedDurationSeconds 271.4
    if ($null -ne $win2.PlaybackSpeed) {
        Write-Output ('FAIL: degenerate 5s wall window produced speed ' + $win2.PlaybackSpeed + ' (must be null after clamp)')
        $pass = $false
    }
    if ($win2.EndUtc -ne [datetime]::MinValue) {
        Write-Output 'FAIL: degenerate window produced an EndUtc'
        $pass = $false
    }
    # Mid-battle early-death shape: a death at wall 40s (02:32:14) with the
    # log then going quiet. The FUNCTION correctly reports a ~6.8x speed and
    # activity - the caller-side monotonic-forward guard (budget/loop: the
    # silence-derived end must be LATER than the current estimate end) is
    # what rejects this early end. This fixture documents that contract so a
    # change to the function can never silently bypass the caller guard.
    $fixture4 = Join-Path $FixtureRoot ('blitz-logs_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    Set-Content -LiteralPath $fixture4 -Value @(
        '02:31:30 [info] 21:31:30 -5 [replay] Start replay event'
        '02:31:34 [info] 21:31:34 -5 [battle] BattleController::LoadGameScene ends'
        '02:32:14 [info] 21:32:14 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549395 isPlayer: 0'
    ) -Encoding ASCII
    (Get-Item -LiteralPath $fixture4).LastWriteTimeUtc = [datetime]::Parse('2026-08-07T02:32:14.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    $script:testFixture = $fixture4
    $win4 = Get-BlitzRealWindow -AnchorUtc $anchor -DecodedDurationSeconds 271.4
    if ($null -eq $win4.PlaybackSpeed -or [Math]::Abs([Math]::Round($win4.PlaybackSpeed, 1) - 6.8) -gt 0.3) {
        Write-Output ('FAIL: mid-battle death window speed ~6.8 expected (the caller monotonic guard rejects this end), got ' + $win4.PlaybackSpeed)
        $pass = $false
    }
    if (-not $win4.BattleActivitySeen) {
        Write-Output 'FAIL: mid-battle death must mark activity (the caller guard needs it)'
        $pass = $false
    }
    # Live-transition shape: no match begin yet, mtime ~= now (game actively
    # writing) -> MatchBeginUtc MinValue + null speed. This is the budget-time
    # reality that must NEVER yield battleEndUtc = now.
    $fixture3 = Join-Path $FixtureRoot ('blitz-logs_' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
    Set-Content -LiteralPath $fixture3 -Value @(
        '02:31:28 [info] 21:31:28 -5 [replay] Start replay event'
        '02:31:28 [info] 21:31:28 -5 [battle] BattleController::LoadGameScene begins'
    ) -Encoding ASCII
    (Get-Item -LiteralPath $fixture3).LastWriteTimeUtc = [datetime]::UtcNow
    $script:testFixture = $fixture3
    $win3 = Get-BlitzRealWindow -AnchorUtc $anchor -DecodedDurationSeconds 271.4
    if ($win3.MatchBeginUtc -ne [datetime]::MinValue) {
        Write-Output 'FAIL: live-transition log produced a match begin (transition scene must be pre-anchor-skipped)'
        $pass = $false
    }
    if ($null -ne $win3.PlaybackSpeed -or $win3.EndUtc -ne [datetime]::MinValue) {
        Write-Output 'FAIL: live-transition log produced end/speed evidence (battleEndUtc would be wrongly = now)'
        $pass = $false
    }
    # Restore the main fixture so ENDED reflects it.
    $script:testFixture = $fixture
    if (-not (Test-BlitzBattleEnded -AnchorUtc $anchor)) {
        Write-Output 'FAIL: Test-BlitzBattleEnded false with a stop-marker window'
        $pass = $false
    }

    if ($pass) { Write-Output 'PASS_blitz_real_window'; $exit = 0 }
    else { Write-Output 'FAIL_blitz_real_window' }
}
finally {
    if (Test-Path -LiteralPath $FixtureRoot) {
        Remove-Item -LiteralPath $FixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exit
