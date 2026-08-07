# Offline validation for the FRESH35 real-window derivation
# (Get-BlitzRealWindow / Test-BlitzBattleEnded / Convert-BlitzLogLineUtc)
# against a SELF-CONTAINED synthetic FRESH34-style blitz log:
#   - hangar->replay transition teardown (player onLeaveWorld isPlayer:1 at
#     02:31:30, BEFORE the real scene) must NOT count as battle end
#   - real battle: last LoadGameScene ends 02:31:34 -> deaths until 02:33:48
#   - the log goes silent at the last death (no stop marker): silence is the
#     end evidence, and playback speed = 271.4 decoded / 134s wall ~= 2.03x
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
# Extract the four functions via PowerShell's own AST parser (robust: the
# manual brace-matcher broke on apostrophes inside comments, e.g. "day's").
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($funcs, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors -and $parseErrors.Count -gt 0) {
    throw ('parse errors in ' + $funcs + ': ' + ($parseErrors[0].Message))
}
$body = ''
foreach ($name in @('Convert-BlitzLogLineUtc', 'Get-BlitzRealWindow', 'Test-BlitzBattleEnded', 'Get-NewestBlitzLog')) {
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
        '02:33:48 [info] 21:33:48 -5 [battle] VehicleGameLogic::onLeaveWorld id:  2549397 isPlayer: 0'
    )
    Set-Content -LiteralPath $fixture -Value $lines -Encoding ASCII
    # mtime = last line time (02:33:48 UTC) so the silence/playback math is
    # consistent with a real log (the silence-20s gate needs LogStaleUtc to
    # be the LAST LINE time, not 'now').
    $target = [datetime]::Parse('2026-08-07T02:33:48.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))
    (Get-Item -LiteralPath $fixture).LastWriteTimeUtc = $target
    # Determinism: override Get-NewestBlitzLog to return the fixture. The
    # extracted function searches the REAL game dir + .data/blitz-logs; the
    # harness must never depend on (or pollute) those live-search paths.
    $script:testFixture = $fixture
    function Global:Get-NewestBlitzLog { return $script:testFixture }

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
    if ($null -eq $win.PlaybackSpeed -or [Math]::Abs([Math]::Round($win.PlaybackSpeed, 2) - 2.03) -gt 0.1) {
        Write-Output ('FAIL: playback speed ~2.03 expected, got ' + $win.PlaybackSpeed)
        $pass = $false
    }
    # The transition teardown (player onLeaveWorld at 02:31:30) must never set
    # EndUtc: EndUtc must be MinValue (silence-based end) or > match begin.
    if ($win.EndUtc -ne [datetime]::MinValue -and $win.EndUtc -le $win.MatchBeginUtc) {
        Write-Output 'FAIL: EndUtc <= match begin - the hangar teardown was misread as battle end'
        $pass = $false
    }
    if (-not $win.BattleActivitySeen) {
        Write-Output 'FAIL: battle activity (onLeaveWorld after match begin) not detected'
        $pass = $false
    }
    # Midnight-crossing unit check: a line 6h+ before the anchor is NOT promoted.
    $crossing = Convert-BlitzLogLineUtc -Line '00:05:00 [info] -5 [battle] VehicleGameLogic::onLeaveWorld id: 1 isPlayer: 0' -Anchor ([datetime]::Parse('2026-08-07T23:58:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) -AnchorDate ([datetime]::Parse('2026-08-07T23:58:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)).Date)
    if ($crossing -ne [datetime]::Parse('2026-08-08T00:05:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) {
        Write-Output ('FAIL: midnight crossing not promoted correctly, got ' + $crossing.ToString('o'))
        $pass = $false
    }
    $preAnchor = Convert-BlitzLogLineUtc -Line '20:00:00 [info] -5 [battle] VehicleGameLogic::onLeaveWorld id: 1 isPlayer: 0' -Anchor ([datetime]::Parse('2026-08-07T21:00:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal))) -AnchorDate ([datetime]::Parse('2026-08-07T21:00:00.0000000Z', [Globalization.CultureInfo]::InvariantCulture, ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)).Date)
    if ($preAnchor -ne [datetime]::MinValue) {
        Write-Output ('FAIL: pre-anchor line (previous battle) not skipped, got ' + $preAnchor.ToString('o'))
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
