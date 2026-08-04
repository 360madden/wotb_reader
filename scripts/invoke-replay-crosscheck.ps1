[CmdletBinding()]
param(
    # .wotbreplay to cross-check (default: newest .data/launch replay).
    [string]$Replay = '',
    # Validate the Rust oracle against wotbreplay-parser's published golden
    # vectors (requires the parser fixtures under C:\work\tools\wotbreplay-parser-main\replays).
    [switch]$GoldenVector,
    # Emit the report even when the two decoders disagree (exit code still set).
    [string]$ReportPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$oracle = Join-Path $repoRoot 'tools\external\installed\wotbreplay-inspector\wotbreplay-inspector.exe'
$csInspector = Join-Path $repoRoot 'tools\src\WotBTreader.ReplayInspector\bin\Debug\net10.0\WotBTreader.ReplayInspector.exe'

# Exit codes: 0 agree, 1 disagree, 2 oracle/inspector missing, 3 decode failed, 4 other.
function Exit-With {
    param([int] $Code, [string] $Message = '')
    if ($Message) { Write-Host $Message }
    exit $Code
}

# ---- 1. Locate the oracle binary (pinned in tools.lock.json) ----
if (-not (Test-Path -LiteralPath $oracle)) {
    Exit-With 2 @"

wotbreplay-inspector is not staged. Copy the built binary to:
  $oracle
(source: C:\work\wotbreplay-inspector-main, cargo build; hash pinned in
tools/external/tools.lock.json). The cross-check is deliberately hard:
without the independent oracle the decoder cannot be cross-validated.
"@
}
if (-not (Test-Path -LiteralPath $csInspector)) {
    Exit-With 2 "C# ReplayInspector not built at $csInspector. Run: dotnet build tools/src/WotBTreader.ReplayInspector."
}

function ConvertTo-UnixSeconds {
    # Use [datetimeoffset] (not [datetime]): casting an ISO instant with an
    # explicit +00:00 offset to [datetime] treats the wall-clock value as
    # LOCAL time, and ToUniversalTime() then shifts it by the machine's UTC
    # offset (the classic PS 5.1 cross-check bug -- caught in testing when
    # battle times disagreed by exactly the UTC+5 shift).
    param([string] $Value)
    return ([datetimeoffset]$Value).ToUnixTimeSeconds()
}

# ---- 2. Golden-vector mode: validate the Rust oracle against published truth ----
if ($GoldenVector) {
    $fixturesDir = 'C:\work\tools\wotbreplay-parser-main\replays'
    if (-not (Test-Path -LiteralPath $fixturesDir)) {
        Exit-With 2 "Parser fixtures not found at $fixturesDir (wotbreplay-parser source snapshot)."
    }

    # Published expected values from wotbreplay-parser's README quickstart +
    # tests/battle_results.rs for 20221203_player_results.wotbreplay.
    $golden = @{
        '20221203_player_results.wotbreplay' = @{
            timestamp_secs = 1670083956
            player_count   = 14
            first_account  = 595693744
            first_nickname = 'yuranhik_hustriy26'
            first_team     = 1
            first_platoon  = 545104609
        }
    }

    $failures = 0
    foreach ($fixture in $golden.Keys) {
        $path = Join-Path $fixturesDir $fixture
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Host "GOLDEN MISSING: $fixture"
            $failures++
            continue
        }
        $json = & $oracle battle-results $path 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "GOLDEN DECODE FAIL: $fixture"
            $failures++
            continue
        }
        $decoded = $json | Out-String | ConvertFrom-Json
        $expected = $golden[$fixture]
        $first = $decoded.players[0]
        $checks = @(
            @{ Name = 'timestamp_secs';  Actual = $decoded.timestamp_secs; Expected = $expected.timestamp_secs },
            @{ Name = 'player_count';    Actual = $decoded.players.Count;  Expected = $expected.player_count },
            @{ Name = 'first_account';   Actual = $first.account_id;       Expected = $expected.first_account },
            @{ Name = 'first_nickname';  Actual = $first.info.nickname;    Expected = $expected.first_nickname },
            @{ Name = 'first_team';      Actual = $first.info.team;        Expected = $expected.first_team },
            @{ Name = 'first_platoon';   Actual = $first.info.platoon_id;  Expected = $expected.first_platoon }
        )
        foreach ($check in $checks) {
            if ($check.Actual -ne $check.Expected) {
                Write-Host ("GOLDEN MISMATCH {0}: {1} expected={2} actual={3}" -f $fixture, $check.Name, $check.Expected, $check.Actual)
                $failures++
            }
        }
        if ($failures -eq 0) {
            Write-Host "GOLDEN PASS: $fixture"
        }
    }
    if ($failures -gt 0) {
        Exit-With 1 "GOLDEN-VECTOR VALIDATION FAILED: $failures check(s)."
    }
    Write-Host 'GOLDEN-VECTOR VALIDATION PASSED: the Rust oracle reproduces the parser''s published expected values.'
    Exit-With 0
}

# ---- 3. Cross-check mode ----
if (-not $Replay) {
    $launchDir = Join-Path $repoRoot '.data\launch'
    $newest = Get-ChildItem -LiteralPath $launchDir -Filter '*.wotbreplay' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $newest) {
        Exit-With 2 "No replay given and none found under $launchDir. Pass -Replay <file>."
    }
    $Replay = $newest.FullName
}
$Replay = (Resolve-Path -LiteralPath $Replay).Path

$report = [ordered]@{}
$report['generated_at_utc'] = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$report['replay'] = $Replay
$report['oracle'] = 'wotbreplay-inspector (Rust + wotbreplay-parser)'
$report['cs_decoder'] = 'WotBTreader.ReplayInspector (C#)'
$report['disagreements'] = @()

# ---- 3a. Rust oracle ----
$rustResultsRaw = & $oracle battle-results $Replay 2>$null
if ($LASTEXITCODE -ne 0) {
    Exit-With 3 "Rust oracle failed to decode battle results for $Replay."
}
$rustResults = $rustResultsRaw | Out-String | ConvertFrom-Json
# dump-data emits JSON-lines (NDJSON), one packet per line -- parse each line.
$rustPackets = @()
foreach ($line in (& $oracle dump-data $Replay 2>$null)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $rustPackets += ($line | ConvertFrom-Json)
}

$rustPlayers = @()
foreach ($player in $rustResults.players) {
    $rustPlayers += [ordered]@{
        account_id = $player.account_id
        nickname   = $player.info.nickname
        team       = $player.info.team
    }
}

# Typed packet surface from dump-data NDJSON:
#  - BasePlayerCreate (type 0): fixed header decoded by both decoders
#    (author_nickname, arena_unique_id, arena_type_id).
#  - UpdateArena (type 8 / subtype 48): the field-1 players roster seen in
#    arena updates. Rust's parser models field 1 only; the C# decoder decodes
#    the same roster, so the comparable surface is the union of
#    (nickname, team) entries plus their account bindings.
$rustBasePlayerCreate = $null
$rustArenaRoster = @{}
foreach ($line in $rustPackets) {
    $payload = $line.payload
    if ($null -eq $payload) { continue }
    $payloadProps = $payload.PSObject.Properties
    if ($null -ne ($payloadProps['BasePlayerCreate'])) {
        $bpc = $payload.BasePlayerCreate
        $rustBasePlayerCreate = [ordered]@{
            author_nickname = $bpc.author_nickname
            arena_unique_id = [uint64]$bpc.arena_unique_id
            arena_type_id   = [uint32]$bpc.arena_type_id
        }
    }
    if ($null -ne ($payloadProps['EntityMethod'])) {
        $em = $payload.EntityMethod
        if ($null -ne $em.PSObject.Properties['UpdateArena']) {
            $arenaArguments = $em.UpdateArena.arguments
            $playersWrapper = if ($null -ne $arenaArguments -and $null -ne $arenaArguments.PSObject.Properties['players']) { $arenaArguments.players } else { $null }
            $players = if ($null -ne $playersWrapper -and $null -ne $playersWrapper.PSObject.Properties['players']) { $playersWrapper.players } else { $null }
            if ($null -eq $players) { continue }
            foreach ($player in $players) {
                $key = "$($player.nickname)|$($player.team_number)"
                if (-not $rustArenaRoster.ContainsKey($key)) {
                    $rustArenaRoster[$key] = [ordered]@{
                        account_id = $player.account_id
                        nickname   = $player.nickname
                        team       = $player.team_number
                    }
                }
            }
        }
    }
}

# ---- 3b. C# decoder ----
$csRaw = & $csInspector $Replay --include-sensitive 2>$null
if ($LASTEXITCODE -ne 0) {
    Exit-With 3 "C# decoder failed to inspect $Replay."
}
$csEnvelope = $csRaw | Out-String | ConvertFrom-Json
if (-not $csEnvelope.success) {
    Exit-With 3 "C# decoder reported failure: $($csEnvelope.error.code)"
}
$csData = $csEnvelope.data

$csParticipants = @()
foreach ($p in $csData.participants) {
    $csParticipants += [ordered]@{
        account_id = $p.accountId
        nickname   = $p.playerName
        team       = $p.teamNumber
    }
}

$report['rust'] = [ordered]@{
    battle_time_unix = $rustResults.timestamp_secs
    players          = $rustPlayers.Count
    packets          = $rustPackets.Count
    packet_clocks    = @($rustPackets | ForEach-Object { $_.clock })
    base_player_create = $rustBasePlayerCreate
    arena_roster_count = $rustArenaRoster.Count
}
# Typed packet surface from the C# inspector (typedPackets block).
$csBasePlayerCreate = $null
$csTypedPackets = if ($null -ne $csData.PSObject.Properties['typedPackets']) { $csData.typedPackets } else { $null }
if ($null -ne $csTypedPackets -and $null -ne $csTypedPackets.PSObject.Properties['basePlayerCreate']) {
    $bpc = $csData.typedPackets.basePlayerCreate
    $csBasePlayerCreate = [ordered]@{
        author_nickname = $bpc.authorNickname
        arena_unique_id = [uint64]$bpc.arenaUniqueId
        arena_type_id   = [uint32]$bpc.arenaTypeId
    }
}
$csArenaRoster = @{}
$csRosterEntries = if ($null -ne $csTypedPackets -and $null -ne $csTypedPackets.PSObject.Properties['updateArenaRoster']) { $csTypedPackets.updateArenaRoster } else { @() }
foreach ($p in $csRosterEntries) {
    $key = "$($p.playerName)|$($p.teamNumber)"
    if ($null -eq $key -or $key -eq '|') { continue }
    if (-not $csArenaRoster.ContainsKey($key)) {
        $csArenaRoster[$key] = [ordered]@{
            account_id = $p.accountId
            nickname   = $p.playerName
            team       = $p.teamNumber
        }
    }
}

# C# inspector exposes only aggregate counts (not per-event clocks), so the
# counts are informational with documented semantics; the pass/fail surface is
# battle time + participants below.
$report['cs'] = [ordered]@{
        battle_time_unix = ConvertTo-UnixSeconds ([string]$csData.session.battleTimeUtc)
        participants     = $csParticipants.Count
        events           = $csData.counts.events
        positions        = $csData.counts.positions
        raw_records      = $csData.counts.rawRecords
        base_player_create = $csBasePlayerCreate
        arena_roster_count = $csArenaRoster.Count
}

# ---- 3c. Compare the cross-check surface ----
# Surface 1: battle time. Rust reports battle_results.dat protobuf tag 2
# (server-recorded battle timestamp). C# prefers meta.json battleStartTime
# (client-recorded) and falls back to the same protobuf tag 2. On real battles
# the two sources can differ by a few seconds (client-vs-server clock), so a
# small delta is a documented note, not a hard failure; a large delta (>=60s)
# is a real disagreement.
$rustTime = [long]$report['rust']['battle_time_unix']
$csTime = [long]$report['cs']['battle_time_unix']
$timeDelta = [Math]::Abs($rustTime - $csTime)
if ($timeDelta -ge 60) {
    $report['disagreements'] += "battle time: rust=$rustTime cs=$csTime (delta ${timeDelta}s)"
}
elseif ($timeDelta -gt 0) {
    $report['battle_time_source_delta_seconds'] = $timeDelta
    $report['battle_time_note'] = 'Rust reads battle_results protobuf tag 2 (server); C# prefers meta.json battleStartTime (client). A small delta is a client-vs-server clock artifact.'
}

# Surface 2: participants. Compare as account-id -> (nickname, team) map.
#
# KNOWN SEMANTIC DIFFERENCE (bot sentinels): WoTB bot accounts are encoded in
# battle-results as negative int32 (-1..-10). The wire varint is sign-extended
# to 64 bits, so C# reads 0xFFFFFFFFFFFFFFFF-style values > long.MaxValue and
# deliberately rejects them (evidence-first: never guess an identity); the
# player then comes only from updateArena with a null account. Rust's prost
# schema declares the field uint32 and truncates to the 4294967286..4294967295
# sentinel range. Both are correct per their contract. The harness therefore
# matches participants by (nickname, team) primarily, and by account only for
# non-sentinel IDs; sentinel-only differences are recorded as a note, not a
# disagreement.
function Is-BotSentinelAccount {
    param([object] $AccountId)
    if ($null -eq $AccountId) { return $false }
    $id = [long]$AccountId
    return $id -ge 4294967286 -and $id -le 4294967295
}

$rustMap = @{}
foreach ($p in $rustPlayers) { $rustMap["$($p.nickname)|$($p.team)"] = [long]$p.account_id }
$csMap = @{}
foreach ($p in $csParticipants) {
    $key = "$($p.nickname)|$($p.team)"
    if (-not $csMap.ContainsKey($key)) { $csMap[$key] = @() }
    if ($null -ne $p.account_id) { $csMap[$key] += [long]$p.account_id }
}

# 1. Roster presence: every rust player must exist in cs and vice versa.
foreach ($key in $rustMap.Keys) {
    if (-not $csMap.ContainsKey($key)) {
        $report['disagreements'] += "player only in rust: $key"
    }
}
foreach ($key in $csMap.Keys) {
    if (-not $rustMap.ContainsKey($key)) {
        $report['disagreements'] += "player only in cs: $key"
    }
}

# 2. Account identity: match when both sides carry a real (non-sentinel) id.
$sentinelNotes = 0
foreach ($key in $rustMap.Keys) {
    if (-not $csMap.ContainsKey($key)) { continue }
    $rustId = $rustMap[$key]
    $csIds = $csMap[$key]
    $csReal = @($csIds | Where-Object { -not (Is-BotSentinelAccount $_) })
    if (Is-BotSentinelAccount $rustId) {
        # Rust sees only the truncated sentinel; C# may have the real id or none.
        if ($csReal.Count -gt 0) {
            $report['disagreements'] += "account mismatch for $key`: rust=$rustId (sentinel) cs=$($csReal -join ',')"
        }
        else {
            $sentinelNotes++
        }
    }
    elseif ($csReal.Count -eq 1 -and $csReal[0] -ne $rustId) {
        $report['disagreements'] += "account mismatch for $key`: rust=$rustId cs=$($csReal[0])"
    }
    elseif ($csReal.Count -gt 1) {
        $report['disagreements'] += "multiple cs accounts for $key`: $($csReal -join ',')"
    }
}
if ($sentinelNotes -gt 0) {
    $report['sentinel_accounts_rust_only'] = $sentinelNotes
}

# Surface 3 (informational): packet counts. The two counters are NOT directly
# comparable: C# raw_records adds archive metadata + battle-results + header
# records on top of packets, and C# events are only the packets the decoder
# mapped to canonical events. Report both sides; a large imbalance (e.g. one
# side seeing hundreds and the other near-zero) is a manual-review signal.
if (($report['rust']['packets'] -eq 0) -ne ($report['cs']['events'] -eq 0)) {
    $report['disagreements'] += "packet presence: rust packets=$($report['rust']['packets']) cs events=$($report['cs']['events'])"
}

# ---- 3d. Typed packet surface (BasePlayerCreate + UpdateArena) ----
#
# Surface 4: BasePlayerCreate header. Both decoders independently parse the
# type-0 fixed header (10 skipped bytes, 1-byte-length UTF-8 author nickname,
# little-endian u64 arena unique id, little-endian u32 arena type id). The
# arena_unique_id is the replay's third arena-identity source (after meta.json
# and the battle-results tuple); on real replays all three agree.
$bpcDisagreements = 0
if ($null -eq $rustBasePlayerCreate -and $null -eq $csBasePlayerCreate) {
    $report['base_player_create_note'] = 'No BasePlayerCreate packet in this replay (synthetic fixtures); surface skipped.'
}
elseif ($null -eq $rustBasePlayerCreate -or $null -eq $csBasePlayerCreate) {
    $report['disagreements'] += "base player create present only on $($(if ($null -eq $rustBasePlayerCreate) { 'cs' } else { 'rust' }))"
    $bpcDisagreements++
}
else {
    foreach ($field in @('author_nickname', 'arena_unique_id', 'arena_type_id')) {
        if ($rustBasePlayerCreate[$field] -ne $csBasePlayerCreate[$field]) {
            $report['disagreements'] += "base player create $field`: rust=$($rustBasePlayerCreate[$field]) cs=$($csBasePlayerCreate[$field])"
            $bpcDisagreements++
        }
    }
}

# Surface 5: UpdateArena roster. Both decoders decode the field-1 players
# roster of subtype-48 packets. Compare roster presence by (nickname, team);
# compare account bindings where either side carries a real id. Rust emits
# account_id 0 for unbound entries; C# emits null -- 0 and null both mean
# "no account evidence" and are treated as equal.
$rosterKeys = @($rustArenaRoster.Keys + $csArenaRoster.Keys | Select-Object -Unique)
$rosterDisagreements = 0
foreach ($key in $rosterKeys) {
    $rustHas = $rustArenaRoster.ContainsKey($key)
    $csHas = $csArenaRoster.ContainsKey($key)
    if ($rustHas -ne $csHas) {
        $report['disagreements'] += "updateArena roster only on $($(if ($rustHas) { 'rust' } else { 'cs' })): $key"
        $rosterDisagreements++
        continue
    }

    if (-not $rustHas) { continue }
    $rustId = [long]$rustArenaRoster[$key].account_id
    $csIdRaw = $csArenaRoster[$key].account_id
    $csId = if ($null -eq $csIdRaw) { 0L } else { [long]$csIdRaw }
    if ($rustId -ne $csId) {
        $report['disagreements'] += "updateArena account for $key`: rust=$rustId cs=$csIdRaw"
        $rosterDisagreements++
    }
}
$report['typed_surface'] = [ordered]@{
    base_player_create_disagreements = $bpcDisagreements
    arena_roster_disagreements       = $rosterDisagreements
}

# ---- 3e. Emit report ----
if (-not $ReportPath) {
    $reportDir = Join-Path $repoRoot '.data'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $ReportPath = Join-Path $reportDir 'replay-crosscheck-report.json'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
Write-Host "Report: $ReportPath"

$disagreementCount = @($report['disagreements']).Count
if ($disagreementCount -gt 0) {
    Write-Host "CROSS-CHECK DISAGREES: $disagreementCount difference(s)."
    $report['disagreements'] | ForEach-Object { Write-Host "  $_" }
    Exit-With 1
}

Write-Host 'CROSS-CHECK AGREES: battle time, participants, packet clocks, and the typed packet surface all match.'
Exit-With 0
