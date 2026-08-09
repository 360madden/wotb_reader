#Requires -Version 5.1
<#
.SYNOPSIS
  G1 live write-observation poll (one approved session): arms the guard-page
  interceptor on the ring-record page the unchanged bounded od-073 poll reads,
  runs the poll inside the capture window, and computes the write-observation
  verdict - zero page writes across the poll read window with liveness on
  both sides (strongest evidence), or the poll's own byte-identical
  double-read consistency (the per-read byte-identical branch). The poll
  script is invoked UNCHANGED; this wrapper only brackets it.

.DESCRIPTION
  Sequence (one new approved live session):
    1. Launcher: launch-offline-replay-for-od.ps1 -ReplayPath ... blocks until
       "OK OfflineReplayVerified" (or fails). Use -WindowWaitSeconds 240 for
       cold boots (the 90s default can fail a cold boot).
    2. Resolve: rendezvous -> /api/v1/game/state (pid) -> sessions/trajectory
       (battle session + viewpoint entity id, unless -SessionId/-EntityId are
       given) -> POST /api/v1/game/discover/position-page (gate-verified,
       fail-closed) for the ring-record address.
    3. Arm: WotBTreader.WriteInterceptor.exe --interceptor -Pid <pid>
       -Addresses 0x<record> -Seconds <window> -Out <report> (x86 publish at
       .build/publish/write-interceptor; the page holding the record is armed
       and every write is captured with post-write value, RVA, and i386
       registers).
    4. Poll: the unchanged od-073 bounded poll (-SessionId passed explicitly)
       runs its stage delay + bounded double-reads inside the capture window.
    5. Verdict: Test-WriteObservationVerdict over [pollStart + stageDelay,
       pollEnd] - the read window. clean = zero interceptor hits in the
       window with liveness (hits) both sides and interceptor exit 0;
       otherwise the evidence record reports the observed-write counts and
       defers to the poll's own allConsistentDoubleRead (the per-read
       byte-identical branch: the resolver only returns Resolved when the two
       56-byte snapshots are identical with a stable ring index).
    6. Evidence: <ResultDir>/g1-evidence.json carries the verdict, the read
       window, hit counts, the interceptor report path, the poll aggregate
       path, and the armed addresses (internal evidence - same class as the
       od-048 family reports; the poll aggregate itself stays privacy-safe).

  Evidence-chain semantics (the grilling review, 2026-08-09):
    - The per-read atomicity proof comes from the POLL, not the interceptor:
      the resolver only returns Resolved when the two 56-byte ring-record
      snapshots are byte-identical with a stable ring index (a mid-read write
      would tear them -> UnstableSnapshot retry). The interceptor arms the
      whole page (PAGE_GUARD granularity) and cannot attribute a hit to the
      exact position bytes, so its role is the complete page write history
      plus the clean-window case.
    - clean (zero page writes across the read window + liveness both sides)
      is a STRONGER global claim; observed is the EXPECTED live outcome for
      a moving entity whose ring slots are rewritten every few frames. An
      observed verdict is not a failure - the per-read byte-identical branch
      is already attested by every Resolved poll read.
    - The launcher marker's owner-only ACL invariant is enforced by the poll
      (Test-OwnerOnlyRendezvousFile inside Get-LaunchArtifactId) and fails
      the poll before any read if the marker is tampered; this wrapper's
      pre-poll discovery uses the marker only to select the session and the
      poll re-validates the binding.

  Offline test of the verdict only (-DryRun):
    powershell -NoProfile -File scripts/invoke-g1-live-poll.ps1 -DryRun `
      -ReportPath .data/diagnostics/g1-mechanism-<stamp>/interceptor-report.json `
      -WindowStartUtc '2026-08-09T18:13:30.5Z' -WindowEndUtc '2026-08-09T18:13:31.5Z'
    picks a window inside a real capture and prints the verdict.

.EXITCODES
  0  Live sequence completed and the verdict was computed (clean OR observed
     are both valid evidence outcomes; the poll's own verdict decides the
     read result).
  2  Usage / environment error (missing publish, bad options).
  3  Launcher failed to reach OfflineReplayVerified.
  4  Position-page / interceptor setup failed.
  5  Unexpected error.
#>
[CmdletBinding()]
param(
    # Replay to launch (live mode). Ignored with -DryRun.
    [string]$ReplayPath = '',
    # Explicit battle session id (skips session discovery).
    [string]$SessionId = '',
    # Explicit viewpoint entity id (skips trajectory discovery).
    [int]$EntityId = 0,
    # Launcher cold-boot window wait (default 240: the 90s default can fail
    # a cold boot with FAILED_no_window).
    [int]$WindowWaitSeconds = 240,
    # Passed through to the unchanged od-073 poll.
    [int]$StageDelaySeconds = 55,
    [int]$ReadCount = 24,
    [int]$ReadIntervalMilliseconds = 750,
    [string[]]$PriorResultPaths = @(),
    # Extra capture after the poll ends (post-window liveness).
    [int]$InterceptorMarginSeconds = 20,
    # Override the x86 interceptor publish path.
    [string]$InterceptorExe = '',
    # Evidence directory (default .data/diagnostics/g1-live-<stamp>).
    [string]$ResultDir = '',
    # Verdict-only mode for offline testing: no game, no launcher, no poll.
    [switch]$DryRun,
    [string]$ReportPath = '',
    [string]$WindowStartUtc = '',
    [string]$WindowEndUtc = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:interceptorProc = $null

function Write-G1([string]$Message) {
    Write-Host ('g1live: ' + $Message)
}

function Cleanup([int]$code) {
    if ($null -ne $script:interceptorProc -and -not $script:interceptorProc.HasExited) {
        Stop-Process -Id $script:interceptorProc.Id -Force -ErrorAction SilentlyContinue
    }
    exit $code
}

# Owner-only ACL invariant on a rendezvous/marker file. Faithful copy of
# od-073's Test-OwnerOnlyRendezvousFile (source of truth - keep in sync). The
# poll re-enforces the same checks before any read; this is defense-in-depth
# for the wrapper's own pre-poll calls.
function Test-OwnerOnlyRendezvousFile([string]$Path) {
    try {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $file = Get-Item -LiteralPath $Path
        $directory = Get-Item -LiteralPath $file.DirectoryName
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }

        $directoryAcl = Get-Acl -LiteralPath $directory.FullName
        $directoryOwner = (New-Object Security.Principal.NTAccount($directoryAcl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $directoryRules = @($directoryAcl.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        if (-not $directoryAcl.AreAccessRulesProtected -or $directoryOwner -ne $owner -or
            $directoryRules.Count -ne 1 -or
            $directoryRules[0].IdentityReference -ne $owner -or
            $directoryRules[0].AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            (($directoryRules[0].FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl)) {
            return $false
        }

        $fileAcl = Get-Acl -LiteralPath $file.FullName
        $fileOwner = (New-Object Security.Principal.NTAccount($fileAcl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $fileRules = @($fileAcl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]))
        return $fileOwner -eq $owner -and $fileRules.Count -eq 1 -and
            $fileRules[0].IdentityReference -eq $owner -and
            $fileRules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($fileRules[0].FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl)
    }
    catch {
        return $false
    }
}

function Get-Rendezvous {
    try {
        $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -eq $file -or
            $file.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-10) -or
            -not (Test-OwnerOnlyRendezvousFile -Path $file.FullName)) {
            return $null
        }

        $value = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (-not $value.PSObject.Properties['baseUri'] -or
            -not $value.PSObject.Properties['capability'] -or
            [string]::IsNullOrWhiteSpace([string]$value.baseUri) -or
            [string]::IsNullOrWhiteSpace([string]$value.capability)) {
            return $null
        }

        $uri = [Uri][string]$value.baseUri
        if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') {
            return $null
        }

        return $value
    }
    catch {
        return $null
    }
}

function Invoke-OdApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )

    $rendezvous = Get-Rendezvous
    if ($null -eq $rendezvous) {
        throw [InvalidOperationException]::new('rendezvous_unavailable')
    }

    $arguments = @{
        Uri        = [string]$rendezvous.baseUri + $RelativePath
        Method     = $Method
        TimeoutSec = 15
        Headers    = @{
            'X-WotBTreader-Capability' = [string]$rendezvous.capability
        }
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 5 -Compress
    }

    return Invoke-RestMethod @arguments
}

function Get-LaunchArtifactId {
    $marker = Join-Path (Join-Path $env:LOCALAPPDATA 'WotBTreader\od-launch') 'artifact.id'
    if (-not (Test-Path -LiteralPath $marker) -or
        -not (Test-OwnerOnlyRendezvousFile -Path $marker)) {
        return $null
    }
    $file = Get-Item -LiteralPath $marker
    if ($file.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-20)) {
        return $null
    }
    return (Get-Content -LiteralPath $marker -Raw).Trim()
}

function Test-WriteObservationVerdict {
    <#
    Computes the write-observation verdict for a capture window against an
    interceptor report. clean = zero hits inside [WindowStart, WindowEnd] with
    at least one hit on each side (1s margins) and interceptor exit 0 - a
    real no-write, not a dead capture. Any other outcome is "observed" (the
    poll's own allConsistentDoubleRead is the per-read byte-identical check).
    #>
    param(
        [string]$ReportPath,
        [DateTimeOffset]$WindowStartUtc,
        [DateTimeOffset]$WindowEndUtc
    )

    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    $hits = @()
    if ($null -ne $report.hits) { $hits = @($report.hits) }

    $inWindow = @($hits | Where-Object {
        [DateTimeOffset]$_.utc -ge $WindowStartUtc -and
        [DateTimeOffset]$_.utc -le $WindowEndUtc
    }).Count
    $before = @($hits | Where-Object {
        [DateTimeOffset]$_.utc -lt $WindowStartUtc.AddSeconds(-1)
    }).Count
    $after = @($hits | Where-Object {
        [DateTimeOffset]$_.utc -gt $WindowEndUtc.AddSeconds(1)
    }).Count

    $reportExit = 0
    if ($null -ne $report.exitCode) { $reportExit = [int]$report.exitCode }
    $clean = ($inWindow -eq 0) -and ($before -ge 1) -and ($after -ge 1) -and
        ($reportExit -eq 0)

    return [ordered]@{
        verdict     = $(if ($clean) { 'write-observation-clean' } else { 'write-observation-observed' })
        clean       = $clean
        inWindow    = $inWindow
        before      = $before
        after       = $after
        reportExit  = $reportExit
        hits        = $hits.Count
        windowStart = $WindowStartUtc.ToString('o')
        windowEnd   = $WindowEndUtc.ToString('o')
        reportPath  = $ReportPath
    }
}

# ---- Dry-run: exercise the verdict against an existing report only ----
if ($DryRun) {
    if ([string]::IsNullOrWhiteSpace($ReportPath) -or
        -not (Test-Path -LiteralPath $ReportPath) -or
        [string]::IsNullOrWhiteSpace($WindowStartUtc) -or
        [string]::IsNullOrWhiteSpace($WindowEndUtc)) {
        Write-Host 'g1live: -DryRun requires -ReportPath, -WindowStartUtc, -WindowEndUtc'
        exit 2
    }
    $start = [DateTimeOffset]::Parse($WindowStartUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    $end = [DateTimeOffset]::Parse($WindowEndUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    $verdict = Test-WriteObservationVerdict -ReportPath $ReportPath -WindowStartUtc $start -WindowEndUtc $end
    $verdict | ConvertTo-Json -Depth 4
    exit $(if ($verdict.clean) { 0 } else { 1 })
}

# ---- Live mode ----
$scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot }
else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

if ([string]::IsNullOrWhiteSpace($ReplayPath)) {
    Write-Host 'g1live: -ReplayPath is required (live mode)'
    exit 2
}
if (-not (Test-Path -LiteralPath $ReplayPath)) {
    Write-Host 'g1live: replay not found'
    exit 2
}
if (-not $InterceptorExe) {
    $InterceptorExe = Join-Path $RepoRoot '.build/publish/write-interceptor/WotBTreader.WriteInterceptor.exe'
}
if (-not (Test-Path -LiteralPath $InterceptorExe)) {
    Write-Host 'g1live: MISSING_EXE build the x86 publish first:'
    Write-Host '  dotnet publish tools/WriteInterceptor -c Release -r win-x86 --self-contained true -o .build/publish/write-interceptor'
    exit 2
}
if (-not $ResultDir) {
    $ResultDir = Join-Path $RepoRoot (Join-Path '.data/diagnostics' ('g1-live-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
}
New-Item -ItemType Directory -Force -Path $ResultDir | Out-Null
$interceptorReportPath = Join-Path $ResultDir 'interceptor-report.json'
$pollAggregatePath = Join-Path $ResultDir 'od073-poll.json'
$evidencePath = Join-Path $ResultDir 'g1-evidence.json'

try {
    # 1. Launcher (blocks until the gate or fails).
    Write-G1 ('launching replay ' + $ReplayPath + ' window_wait_s=' + $WindowWaitSeconds)
    & (Join-Path $scriptDir 'launch-offline-replay-for-od.ps1') `
        -ReplayPath $ReplayPath `
        -WindowWaitSeconds $WindowWaitSeconds
    $launcherExit = $LASTEXITCODE
    Write-G1 ('launcher_exit=' + $launcherExit)
    if ($launcherExit -ne 0) {
        Write-Host 'g1live: FAIL_launcher_gate'
        exit 3
    }

    # 2. Rendezvous -> state -> session -> entity -> position page.
    $state = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/game/state'
    $gamePid = [int]$state.pid
    Write-G1 ('game_pid=' + $gamePid)

    $launchArtifactId = Get-LaunchArtifactId
    if ([string]::IsNullOrWhiteSpace($launchArtifactId)) {
        Write-Host 'g1live: FAIL_launch_artifact_binding'
        exit 4
    }

    $battleSessionId = $SessionId
    if ([string]::IsNullOrWhiteSpace($battleSessionId)) {
        $page = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
        $artifactSessions = @($page.items | Where-Object {
            $null -ne $_.session -and
            [string]$_.decodeRun.sourceArtifactId -eq $launchArtifactId
        })
        if ($artifactSessions.Count -eq 0) {
            Write-Host 'g1live: FAIL_no_decoded_session'
            exit 4
        }
        $battleSessionId = [string]$artifactSessions[0].session.battleSessionId
    }
    Write-G1 ('battle_session=' + $battleSessionId)

    $entityId = $EntityId
    if ($entityId -le 0) {
        $trajectory = Invoke-OdApi -Method 'Get' -RelativePath (
            '/api/v1/game/discover/trajectory/' + $battleSessionId)
        $viewpoints = @($trajectory.entities | Where-Object {
            $_.isViewpoint -eq $true -and $null -ne $_.entityId
        })
        if ($viewpoints.Count -ne 1) {
            Write-Host 'g1live: FAIL_viewpoint_ground_truth'
            exit 4
        }
        $entityId = [int]$viewpoints[0].entityId
    }
    Write-G1 ('entity_id=' + $entityId)

    $positionPage = Invoke-OdApi -Method 'Post' `
        -RelativePath '/api/v1/game/discover/position-page' `
        -Body @{ entityId = $entityId }
    if ([string]$positionPage.status -ne 'Resolved' -or
        [string]::IsNullOrWhiteSpace($positionPage.recordAddress)) {
        Write-Host ('g1live: FAIL_position_page status=' + $positionPage.status)
        exit 4
    }
    Write-G1 ('record_address=' + $positionPage.recordAddress +
        ' page=' + $positionPage.pageAddress)

    # 3. Interceptor window covers stage delay + reads + margins.
    $interceptorSeconds = $StageDelaySeconds + ($ReadCount * 2) +
        $InterceptorMarginSeconds + 10
    Write-G1 ('interceptor_seconds=' + $interceptorSeconds)
    $script:interceptorProc = Start-Process -FilePath $InterceptorExe `
        -ArgumentList @('--interceptor', '-Pid', ([string]$gamePid), '-Addresses', [string]$positionPage.recordAddress, '-Seconds', ([string]$interceptorSeconds), '-Out', $interceptorReportPath) `
        -PassThru -WindowStyle Hidden
    Write-G1 ('interceptor_started pid=' + $script:interceptorProc.Id)
    Start-Sleep -Seconds 2

    # 4. Unchanged bounded poll inside the capture window.
    $pollStartUtc = [DateTimeOffset]::UtcNow
    Write-G1 'starting unchanged od073 poll'
    & (Join-Path $scriptDir 'od-073-entity-position-poll.ps1') `
        -SessionId $battleSessionId `
        -StageDelaySeconds $StageDelaySeconds `
        -ReadCount $ReadCount `
        -ReadIntervalMilliseconds $ReadIntervalMilliseconds `
        -ResultPath $pollAggregatePath `
        -PriorResultPaths $PriorResultPaths
    $pollExit = $LASTEXITCODE
    $pollEndUtc = [DateTimeOffset]::UtcNow
    Write-G1 ('poll_exit=' + $pollExit)

    # 5. Let the interceptor finish its post-poll margin, then collect.
    if ($null -ne $script:interceptorProc -and -not $script:interceptorProc.HasExited) {
        try {
            Wait-Process -Id $script:interceptorProc.Id -Timeout 60 -ErrorAction Stop
        }
        catch {
            Write-G1 'interceptor_still_running_stopping'
            Stop-Process -Id $script:interceptorProc.Id -Force -ErrorAction SilentlyContinue
        }
    }
    $script:interceptorProc = $null
    if (-not (Test-Path -LiteralPath $interceptorReportPath)) {
        Write-Host 'g1live: FAIL_missing_interceptor_report'
        exit 4
    }

    # 6. Verdict over the read window [pollStart + stageDelay, pollEnd].
    $readWindowStart = $pollStartUtc.AddSeconds($StageDelaySeconds)
    $verdict = Test-WriteObservationVerdict `
        -ReportPath $interceptorReportPath `
        -WindowStartUtc $readWindowStart `
        -WindowEndUtc $pollEndUtc

    $pollSucceeded = ($pollExit -eq 0)

    $evidence = [ordered]@{
        schema = 'wotbtreader.g1.write-observation.v1'
        campaign = 'g1-hardware-atomicity'
        createdUtc = [DateTime]::UtcNow.ToString('o')
        pollExit = $pollExit
        pollSucceeded = $pollSucceeded
        pollAggregatePath = $pollAggregatePath
        interceptorReportPath = $interceptorReportPath
        gamePid = $gamePid
        battleSessionId = $battleSessionId
        entityId = $entityId
        armedRecordAddress = [string]$positionPage.recordAddress
        armedPageAddress = [string]$positionPage.pageAddress
        interceptorSeconds = $interceptorSeconds
        pollStartUtc = $pollStartUtc.ToString('o')
        pollEndUtc = $pollEndUtc.ToString('o')
        readWindowStartUtc = $readWindowStart.ToString('o')
        verdict = $verdict
        privacy = [ordered]@{
            entityIdsPersisted = $false
            coordinatesPersisted = $false
            processAddressesPersisted = $true
            rawBytesPersisted = $false
            capabilityPersisted = $false
        }
    }
    $evidence | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $evidencePath -Encoding UTF8

    Write-G1 ('verdict=' + $verdict.verdict +
        ' in_window=' + $verdict.inWindow +
        ' before=' + $verdict.before +
        ' after=' + $verdict.after +
        ' poll_exit=' + $pollExit +
        ' evidence=' + $evidencePath)
    Write-G1 'runbook: stop the game + Host (managed processes) after reviewing the evidence'
    if (-not $pollSucceeded) {
        Write-Host ('g1live: FAIL_poll_exit=' + $pollExit + ' (evidence written; G1 claim requires a successful poll)')
        exit $pollExit
    }
    exit 0
}
catch {
    Write-Host ('g1live: FAIL_unexpected ' + $_.Exception.Message)
    Cleanup 5
}
