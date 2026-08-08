[CmdletBinding()]
param(
    # .wotbreplay to launch for the cross-battle round (default: the staged
    # savanna replay). The replay MUST hash to the OD-RECOVERY-058 digest - a
    # tampered or wrong file is refused fail-closed.
    [string]$ReplayPath = '',
    # Result JSON written by the od-049 driver (default: .data\od-049-fresh44-crossbattle-result.json).
    [string]$ResultPath = '',
    # Run ONLY the checks (replay hash, artifacts, decoded ground truth) and
    # print what would launch. Never starts the game or the host.
    [switch]$CheckOnly,
    # Keep the game window after the campaign (passed through to the driver;
    # the driver normally stops it to avoid the OD-044 replay-loop flake).
    [switch]$KeepGame,
    # FRESH45 changed hypothesis: direct candidate-derived position-triple
    # batch read immediately after final correlation.
    [switch]$ImmediatePositionTripleRead,
    # Skip the delayed write trace when the round is scoped to the immediate
    # read hypothesis.
    [switch]$SkipAutoWriteTrace,
    # Dedicated tee path. Supplying this lets a follow-up round preserve the
    # accepted FRESH44 log instead of overwriting it.
    [string]$LogPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Exit codes: 0 checks+round complete, 1 replay missing/hash mismatch,
# 2 preflight failure (driver/host/ground truth), 3 driver launch failed,
# 4 other failure.
function Exit-With {
    param([int] $Code, [string] $Message = '')
    if ($Message) { Write-Host $Message }
    exit $Code
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ReplayPath)) {
    $ReplayPath = Join-Path $repoRoot '.data\launch\savanna-20260802-crossbattle.wotbreplay'
}
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $repoRoot '.data\od-049-fresh44-crossbattle-result.json'
}

# OD-RECOVERY-058: the second independent 11.19.0 replay (savanna, 2026-08-02,
# same player mrkool1138 + same tank GB08_Churchill_I as FRESH43).
$ExpectedSha256 = '0fae5612491e0151e9b9d9590a1424d5e00abbd339199df0361f82fe16f69ec1'
$ExpectedBytes = 1045525
$ExpectedSessionId = '019fdff7-8dcf-7426-8547-9fb8cc3eb07b'

$driver = Join-Path $repoRoot 'tmpwotb-e2e\od-049-autoloop.ps1'
$launchScript = Join-Path $repoRoot 'scripts\launch-offline-replay-for-od.ps1'
$hostDll = Join-Path $repoRoot 'src\WotBTreader.Host.Web\bin\Release\net10.0\WotBTreader.Host.Web.dll'
$interceptorExe = Join-Path $repoRoot '.build\publish\write-interceptor\WotBTreader.WriteInterceptor.exe'
$dbPath = Join-Path $repoRoot '.data\treader.db'
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $repoRoot '.data\od-049-fresh44-crossbattle.log'
}

function Convert-ToSafeLogText([string]$Value) {
    $safe = $Value
    $replacements = @(
        @{ Value = $ReplayPath; Token = '<replay>' }
        @{ Value = $ResultPath; Token = '<result>' }
        @{ Value = $LogPath; Token = '<log>' }
        @{ Value = $repoRoot; Token = '<repo>' }
        @{ Value = $env:LOCALAPPDATA; Token = '<localappdata>' }
        @{ Value = $env:TEMP; Token = '<temp>' }
    )
    foreach ($replacement in $replacements) {
        if (-not [string]::IsNullOrWhiteSpace([string]$replacement.Value)) {
            $safe = [regex]::Replace(
                $safe,
                [regex]::Escape([string]$replacement.Value),
                [string]$replacement.Token,
                [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    $safe = [regex]::Replace($safe, '(?i)\b[A-Z]:\\[^\s''"<>|]+', '<local-path>')
    return $safe
}

# Treat instrumentation failures as launch failures while preserving honest
# scientific negatives (no eligible candidate, mismatch, or gap exceeded).
function Test-ImmediateEvidenceResult {
    param([object]$Result, [bool]$Required)
    if (-not $Required) { return [pscustomobject]@{ Ok = $true; Reason = '' } }
    if ($null -eq $Result -or -not $Result.PSObject.Properties['immediatePositionTripleRead']) {
        return [pscustomobject]@{ Ok = $false; Reason = 'immediate evidence field missing' }
    }
    $immediate = $Result.immediatePositionTripleRead
    if ($null -eq $immediate -or -not $immediate.PSObject.Properties['enabled'] -or -not [bool]$immediate.enabled) {
        return [pscustomobject]@{ Ok = $false; Reason = 'immediate evidence not enabled' }
    }
    if (-not $immediate.PSObject.Properties['status'] -or [string]::IsNullOrWhiteSpace([string]$immediate.status)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'immediate evidence status missing' }
    }
    $status = [string]$immediate.status
    if ($status -in @('read-failed', 'analysis-failed', 'not-attempted')) {
        return [pscustomobject]@{ Ok = $false; Reason = ('immediate instrumentation failure: ' + $status) }
    }
    if ($status -eq 'no-eligible-viewpoint-x-candidate') {
        return [pscustomobject]@{ Ok = $true; Reason = '' }
    }
    if ($status -ne 'complete' -or -not $immediate.PSObject.Properties['verdict']) {
        return [pscustomobject]@{ Ok = $false; Reason = ('unexpected immediate evidence shape/status: ' + $status) }
    }
    $verdict = [string]$immediate.verdict
    if ($verdict -notin @('hypothesis-match-within-gap', 'hypothesis-match-gap-exceeded', 'no-hypothesis-match')) {
        return [pscustomobject]@{ Ok = $false; Reason = ('unexpected immediate verdict: ' + $verdict) }
    }
    return [pscustomobject]@{ Ok = $true; Reason = '' }
}

Write-Host 'fresh44: repo=<repo>'
Write-Host 'fresh44: replay=<replay>'

# ---- 1. Replay hash + size gate (fail-closed) ----
if (-not (Test-Path -LiteralPath $ReplayPath)) {
    Exit-With 1 @"

fresh44: replay NOT FOUND: <replay>

Stage the second independent replay described by OD-RECOVERY-058 under
  .data\launch\savanna-20260802-crossbattle.wotbreplay
"@
}
$fileInfo = Get-Item -LiteralPath $ReplayPath
if ($fileInfo.Length -ne $ExpectedBytes) {
    Exit-With 1 "fresh44: replay SIZE mismatch (got $($fileInfo.Length), expected $ExpectedBytes) - wrong file, refused."
}
$actualSha = (Get-FileHash -LiteralPath $ReplayPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha -ne $ExpectedSha256) {
    Exit-With 1 'fresh44: replay HASH mismatch - tampered or wrong file, refused.'
}
Write-Host "fresh44: replay identity OK bytes=$($fileInfo.Length)"

# ---- 2. Artifact preflight (driver, launch script, host build) ----
foreach ($p in @($driver, $launchScript)) {
    if (-not (Test-Path -LiteralPath $p)) { Exit-With 2 "fresh44: required script NOT FOUND: $(Split-Path -Leaf $p)" }
}
if (-not (Test-Path -LiteralPath $hostDll)) {
    Exit-With 2 'fresh44: Host.Web Release build missing - run: dotnet build -c Release.'
}
$newestSource = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.csproj' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -ne $newestSource -and $newestSource.LastWriteTime -gt (Get-Item -LiteralPath $hostDll).LastWriteTime) {
    Exit-With 2 "fresh44: Host.Web build is STALE (newer source: $($newestSource.Name)) - rebuild Release first."
}
if (-not $SkipAutoWriteTrace) {
    if (-not (Test-Path -LiteralPath $interceptorExe)) {
        Exit-With 2 'fresh44: WriteInterceptor publish missing - republish Release win-x86 first.'
    }
    $newestInterceptorSource = Get-ChildItem -Path (Join-Path $repoRoot 'tools\WriteInterceptor') -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $newestInterceptorSource -and $newestInterceptorSource.LastWriteTime -gt (Get-Item -LiteralPath $interceptorExe).LastWriteTime) {
        Exit-With 2 "fresh44: WriteInterceptor publish is STALE (newer source: $($newestInterceptorSource.Name)) - republish Release win-x86 first."
    }
}
$artifactSummary = if ($SkipAutoWriteTrace) { 'driver + launch script + fresh Host.Web; trace skipped' } else { 'driver + launch script + fresh Host.Web + WriteInterceptor' }
Write-Host ('fresh44: artifacts OK (' + $artifactSummary + ')')

# ---- 3. Decoded ground-truth preflight (the correlate needs this session) ----
if (-not (Test-Path -LiteralPath $dbPath)) {
    Exit-With 2 'fresh44: decoded DB NOT FOUND - import the savanna replay first.'
}
$probePy = @'
import sqlite3, sys
con = sqlite3.connect("file:{db}?mode=ro", uri=True)
cur = con.cursor()
cur.execute("""
    SELECT bs.id, bs.game_version, bs.map_name, bs.battle_time_utc,
           count(DISTINCT p.id), (SELECT count(*) FROM position_samples ps WHERE ps.battle_session_id = bs.id)
    FROM battle_sessions bs
    JOIN decode_runs dr ON dr.id = bs.decode_run_id
    JOIN source_artifacts sa ON sa.id = dr.source_artifact_id
    LEFT JOIN participants p ON p.battle_session_id = bs.id
    WHERE sa.sha256 = ?
    GROUP BY bs.id
""", ("{sha}",))
row = cur.fetchone()
if row is None:
    print("MISSING")
else:
    print("|".join([str(x) for x in row]))
'@
# NOTE: in PowerShell single quotes '\\' is TWO backslashes; a single
# backslash literal is '\' - this converts the Windows DB path to forward
# slashes so the embedded Python string has no invalid \w/\U escapes.
$probePy = $probePy.Replace('{db}', $dbPath.Replace('\', '/')).Replace('{sha}', $ExpectedSha256)
# 2>$null: python's stderr must NOT merge into $probe - a warning would turn
# $probe into an array and the -split below would index wrong elements (and
# Set-StrictMode throws on out-of-range indexes). $LASTEXITCODE already catches
# real python failures.
$probe = ($probePy | python -) 2>$null
if ($LASTEXITCODE -ne 0 -or $probe -eq 'MISSING') {
    Exit-With 2 "fresh44: replay NOT decoded in treader.db - import it first. probe=$probe"
}
$parts = $probe -split '\|'
Write-Host ("fresh44: decoded ground truth OK session=" + $parts[0] + " ver=" + $parts[1] + " map=" + $parts[2] + " participants=" + $parts[4] + " samples=" + $parts[5])
if ($parts[0] -ne $ExpectedSessionId) {
    Write-Host ("fresh44: WARNING decoded session id " + $parts[0] + " differs from expected " + $ExpectedSessionId + " (continuing with the DB row)")
}

if ($CheckOnly) {
    Write-Host ''
    Write-Host 'fresh44: CHECK-ONLY - all gates green, NOT launching.'
    Write-Host ("fresh44: would run: driver=" + (Split-Path -Leaf $driver))
    Write-Host ("fresh44: would write: result=" + (Split-Path -Leaf $ResultPath) + " log=" + (Split-Path -Leaf $LogPath))
    Exit-With 0
}

# Every live invocation must produce fresh, unambiguous evidence. Refuse
# existing destinations instead of overwriting or accidentally accepting a
# stale result from an earlier run.
foreach ($output in @($ResultPath, $LogPath)) {
    if (Test-Path -LiteralPath $output) {
        Exit-With 2 ('fresh44: output already exists; choose a fresh name: ' + (Split-Path -Leaf $output))
    }
}

# ---- 4. Launch the cross-battle round (driver handles host lease + game) ----
$driverArgs = @{
    RepoRoot                = $repoRoot
    ReplayPath              = $ReplayPath
    AttachSmokeOnFirstRound = $true
    StageViewpointOnly      = $true
    PlaybackSpeedEstimate   = 2.4
    StageMinBattleSeconds   = 30
    AutoTraceSeconds        = 25
    ResultPath              = $ResultPath
}
if (-not $SkipAutoWriteTrace) { $driverArgs.ArmSourceOnFirstHit = $true }
if ($ImmediatePositionTripleRead) { $driverArgs.ImmediatePositionTripleRead = $true }
if ($SkipAutoWriteTrace) { $driverArgs.SkipAutoWriteTrace = $true }
if ($KeepGame) { $driverArgs.KeepGame = $true }

$runMode = if ($SkipAutoWriteTrace) { 'host lease + game + correlate + immediate read; trace skipped' } else { 'host lease + game + correlate + auto-trace' }
Write-Host ('fresh44: launching driver (' + $runMode + '). Live progress:')
Write-Host 'fresh44: ------------------------------------------------------------------'
$startedUtc = [DateTime]::UtcNow
# Tee-Object persists the full driver stream to $LogPath AND passes each line
# through to the host so the operator sees live progress (the old pattern only
# Write-Host'ed the stream, leaving the claimed log path a dead file).
& $driver @driverArgs *>&1 |
    ForEach-Object { Convert-ToSafeLogText -Value ([string]$_) } |
    Tee-Object -FilePath $LogPath |
    ForEach-Object { Write-Host ('fresh44: ' + $_) }
$driverExit = $LASTEXITCODE
Write-Host 'fresh44: ------------------------------------------------------------------'
Write-Host ("fresh44: driver_exit=" + $driverExit + " elapsed_s=" + [Math]::Round(([DateTime]::UtcNow - $startedUtc).TotalSeconds, 1))
if (-not $KeepGame) {
    $researchHosts = @(Get-Process -Name 'WotBTreader.Host.Web' -ErrorAction SilentlyContinue)
    foreach ($researchHost in $researchHosts) {
        Stop-Process -Id $researchHost.Id -Force -ErrorAction SilentlyContinue
    }
    if ($researchHosts.Count -gt 0) {
        Write-Host 'fresh44: cleanup: stopped research host'
    }
}
if ($driverExit -ne 0) {
    Exit-With 3 "fresh44: driver FAILED (exit $driverExit) - see log: $(Split-Path -Leaf $LogPath)"
}

# ---- 5. Report result + newest capture artifacts ----
if (-not (Test-Path -LiteralPath $ResultPath)) {
    Exit-With 4 "fresh44: driver reported success but result NOT FOUND: $(Split-Path -Leaf $ResultPath)"
}
$resultFile = Get-Item -LiteralPath $ResultPath
if ($resultFile.LastWriteTimeUtc -lt $startedUtc.AddSeconds(-2)) {
    Exit-With 4 ('fresh44: result is stale for this invocation: ' + $resultFile.Name)
}
Write-Host ''
Write-Host 'fresh44: ===== ROUND COMPLETE ===='
Write-Host ("fresh44: result      : " + (Split-Path -Leaf $ResultPath))
Write-Host ("fresh44: autoloop log: " + (Split-Path -Leaf $LogPath))
$dataDir = Join-Path $repoRoot '.data'
if ($SkipAutoWriteTrace) {
    Write-Host 'fresh44: trace       : skipped'
}
else {
    $newest = @(Get-ChildItem -Path $dataDir -Filter 'od-048-autotrace-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 3)
    foreach ($trace in $newest) {
        Write-Host ("fresh44: trace       : " + $trace.Name)
        foreach ($suffix in '.capture.json', '.family.json') {
            $side = $trace.FullName + $suffix
            if (Test-Path -LiteralPath $side) { Write-Host ("fresh44:            : " + (Split-Path $side -Leaf)) }
        }
    }
}
try { $result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json }
catch { Exit-With 4 'fresh44: result JSON malformed; evidence rejected.' }

$immediateCheck = Test-ImmediateEvidenceResult -Result $result -Required ([bool]$ImmediatePositionTripleRead)
if (-not $immediateCheck.Ok) {
    Exit-With 4 ('fresh44: ' + $immediateCheck.Reason + '; evidence rejected.')
}

try {
    $cor = $result.correlate
    if ($null -ne $cor) {
        Write-Host ("fresh44: correlate: addressed_scored=" + $cor.addressesScored + " strong_by_axis x=" + $cor.strongByAxis.x + " y=" + $cor.strongByAxis.y + " z=" + $cor.strongByAxis.z)
    }
    if ($result.PSObject.Properties['immediatePositionTripleRead'] -and $null -ne $result.immediatePositionTripleRead) {
        $immediate = $result.immediatePositionTripleRead
        $immediateVerdict = if ($immediate.PSObject.Properties['verdict']) { [string]$immediate.verdict } else { [string]$immediate.status }
        $immediateMatches = if ($immediate.PSObject.Properties['matchingCandidateCount']) { [int]$immediate.matchingCandidateCount } else { 0 }
        $immediateGap = if ($immediate.PSObject.Properties['completionGapMilliseconds']) { [string]$immediate.completionGapMilliseconds } else { 'n/a' }
        Write-Host ('fresh44: immediate : verdict=' + $immediateVerdict + ' matches=' + $immediateMatches + ' completion_gap_ms=' + $immediateGap + ' object_base_proven=false')
    }
    $strong = @($result.strongSurvivors | Select-Object -First 5)
    foreach ($s in $strong) {
        Write-Host ("fresh44: survivor  : " + $s.address + " axis=" + $s.axis + " score=" + $s.score + " shift=" + $s.shiftSeconds + "s match=" + $s.matchCount + "/" + $s.totalSamples)
    }
    if ($strong.Count -eq 0) { Write-Host 'fresh44: no strong survivors this round' }
}
catch {
    Exit-With 4 'fresh44: result summary shape malformed; evidence rejected.'
}
Exit-With 0
