<#
.SYNOPSIS
    Normalize Cheat Engine discovery output and update offset evidence safely.

.DESCRIPTION
    Checks offline-replay prerequisites, waits for a human-run Cheat Engine
    session to produce discovered-offsets-multiscan.json, accepts both the
    autoDiscover() fieldResults shape and the legacy saveDiscovered() shape,
    computes the executable hash, and merges only uniquely valid module-relative candidates with complete module identity.

    A candidate never becomes Verified here. Existing nonzero offsets are never
    replaced by a different candidate; stale/quarantined fields and conflicting
    or unclassified results remain report-only.

.PARAMETER GameVersion
    Target game version, for example 11.19.0.10. Auto-detected when omitted.

.PARAMETER CeExePath
    Cheat Engine executable path. Auto-detected when omitted.

.PARAMETER LuaScript
    Lua script path. Defaults to tools/cheat-engine/multiscan.lua.

.PARAMETER SkipValidation
    Skip the optional Python offset validator.

.PARAMETER DryRun
    Check prerequisites only.

.PARAMETER SelfTest
    Run synthetic, offline-only checks for normalization, conflict handling,
    evidence de-duplication, and existing-table validation.
#>

[CmdletBinding()]
param(
    [string]$GameVersion,
    [string]$CeExePath,
    [string]$LuaScript,
    [switch]$SkipValidation,
    [switch]$DryRun,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$KnownOffsetFields = @(
    'replayTime', 'playerHP', 'playerPositionX', 'playerPositionY',
    'playerPositionZ', 'playerYaw', 'cameraPitch', 'aliveTankCount'
)

function Get-CheatEnginePath {
    $candidates = @(
        'C:\Program Files\Cheat Engine 7.7\cheatengine-x86_64.exe',
        'C:\Program Files\Cheat Engine 7.5\cheatengine-x86_64.exe',
        'C:\Program Files\Cheat Engine\cheatengine-x86_64.exe',
        'C:\Program Files (x86)\Cheat Engine 7.7\cheatengine-x86_64.exe',
        'C:\Program Files (x86)\Cheat Engine 7.5\cheatengine-x86_64.exe'
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }

    $fromPath = Get-Command cheatengine-x86_64.exe -ErrorAction SilentlyContinue
    if ($null -ne $fromPath) { return $fromPath.Source }
    return $null
}

function Get-GameExe {
    $candidates = @(
        'C:\Games\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\Steam\steamapps\common\World of Tanks Blitz\wotblitz.exe',
        (Join-Path $env:LOCALAPPDATA 'World_of_Tanks_Blitz\wotblitz.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate -PathType Leaf) {
            $item = Get-Item $candidate
            return [pscustomobject]@{
                Path = $candidate
                Version = $item.VersionInfo.ProductVersion
            }
        }
    }
    return $null
}

function Get-DiscoveryBatches {
    param([object]$Data)

    $batches = @()
    if ($null -ne $Data.fieldResults) {
        foreach ($property in $Data.fieldResults.PSObject.Properties) {
            $result = $property.Value
            $candidates = if ($null -ne $result.candidates) { @($result.candidates) } else { @() }
            $reported = if ($null -ne $result.totalCandidates) {
                [int]$result.totalCandidates
            } else { $candidates.Count }
            $batches += [pscustomobject]@{
                FieldName = $property.Name
                Candidates = $candidates
                ReportedCandidateCount = $reported
                ModuleName = [string]$Data.moduleName
                ModuleBase = [string]$Data.moduleBase
                ModuleSize = $Data.moduleSize
            }
        }
    } elseif ($null -ne $Data.candidates -and
        -not [string]::IsNullOrWhiteSpace([string]$Data.fieldName)) {
        $candidates = @($Data.candidates)
        $reported = if ($null -ne $Data.totalCandidates) {
            [int]$Data.totalCandidates
        } else { $candidates.Count }
        $batches += [pscustomobject]@{
            FieldName = [string]$Data.fieldName
            Candidates = $candidates
            ReportedCandidateCount = $reported
            ModuleName = [string]$Data.moduleName
            ModuleBase = [string]$Data.moduleBase
            ModuleSize = $Data.moduleSize
        }
    }

    return $batches
}

function Convert-CandidateOffset {
    param([object]$Candidate)

    $decimalOffset = $null
    $decimalProperty = $Candidate.PSObject.Properties['relativeOffsetDecimal']
    if ($null -ne $decimalProperty -and $null -ne $decimalProperty.Value) {
        $value = $decimalProperty.Value
        $numericTypes = @(
            [byte], [sbyte], [uint16], [int16], [uint32], [int],
            [uint64], [long], [single], [double], [decimal]
        )
        if ($value -is [string] -or $value -is [bool] -or $value -is [array] -or
            $numericTypes -notcontains $value.GetType()) {
            return $null
        }
        try { $decimal = [decimal]$value } catch { return $null }
        if ($decimal -ne [decimal]::Truncate($decimal) -or
            $decimal -le 0 -or $decimal -gt 0x7FFFFFFF) {
            return $null
        }
        $decimalOffset = [long]$decimal
    }

    $hexOffset = $null
    $relativeProperty = $Candidate.PSObject.Properties['relativeOffset']
    if ($null -ne $relativeProperty -and $null -ne $relativeProperty.Value) {
        if ($relativeProperty.Value -isnot [string] -or
            [string]$relativeProperty.Value -notmatch '^0x([0-9a-fA-F]+)$') {
            return $null
        }
        try {
            $offset = [Convert]::ToInt64($matches[1], 16)
            if ($offset -le 0 -or $offset -gt 0x7FFFFFFF) { return $null }
            $hexOffset = $offset
        } catch { return $null }
    }

    # CE output may contain both forms. Never silently prefer one: a mismatch
    # means the candidate record is internally inconsistent and is rejected.
    if ($null -ne $decimalOffset -and $null -ne $hexOffset -and
        $decimalOffset -ne $hexOffset) {
        return $null
    }
    if ($null -ne $decimalOffset) { return $decimalOffset }
    return $hexOffset
}

function Convert-HexAddress {
    param([object]$Value)

    if ($null -eq $Value -or $Value -isnot [string] -or
        [string]$Value -notmatch '^0x([0-9a-fA-F]+)$') { return $null }
    try { return [Convert]::ToInt64($matches[1], 16) } catch { return $null }
}

function Test-ModuleRelativeCandidate {
    param(
        [object]$Candidate,
        [object]$Batch
    )

    if ([string]$Batch.ModuleName -ine 'wotblitz.exe') { return $false }
    $base = Convert-HexAddress $Batch.ModuleBase
    $absolute = Convert-HexAddress $Candidate.absoluteAddress
    $size = 0L
    try { $size = [long]$Batch.ModuleSize } catch { return $false }
    if ($null -eq $base -or $null -eq $absolute -or $size -le 0) { return $false }
    if ($absolute -lt $base -or $absolute -ge ($base + $size)) { return $false }

    $normalized = Convert-CandidateOffset $Candidate
    return $null -ne $normalized -and ($absolute - $base) -eq $normalized
}

function Get-NormalizedCandidates {
    param([object]$Batch)

    $normalized = @()
    foreach ($candidate in @($Batch.Candidates)) {
        $offset = Convert-CandidateOffset $candidate
        if ($null -ne $offset -and $offset -gt 0 -and $offset -le 0x7FFFFFFF) {
            $normalized += [pscustomobject]@{
                FieldName = $Batch.FieldName
                Offset = $offset
                AddressKind = if (Test-ModuleRelativeCandidate $candidate $Batch) { 'module-rva' } else { 'heap-dynamic-or-unclassified' }
                CandidateCount = @($Batch.Candidates).Count
                ReportedCandidateCount = $Batch.ReportedCandidateCount
            }
        }
    }
    return $normalized
}

function Assert-RequestedGameVersion {
    param(
        [string]$RequestedVersion,
        [string]$DetectedVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedVersion) -and
        $RequestedVersion -cne $DetectedVersion) {
        throw "Requested game version '$RequestedVersion' does not match installed executable version '$DetectedVersion'."
    }
}

function Ensure-ValidationEntry {
    param(
        [object]$Table,
        [string]$FieldName
    )

    $validationProperty = $Table.fieldValidation.PSObject.Properties[$FieldName]
    if ($null -eq $validationProperty) {
        $Table.fieldValidation | Add-Member -NotePropertyName $FieldName -NotePropertyValue ([pscustomobject]@{
            status = 'Candidate'
            evidence = @()
            independentProcessLaunches = 0
            independentReplays = 0
            harnessInvariantsPassed = $false
            leadApproved = $false
            decoderAuditorApproved = $false
        }) -Force
    }
    return $Table.fieldValidation.$FieldName
}

function Publish-DynamicCandidate {
    param(
        [object]$Table,
        [string]$FieldName,
        [long]$Offset,
        [string]$Notes
    )

    if ($FieldName -notin $KnownOffsetFields) { return 'unknown-field' }
    $offsetProperty = $Table.offsets.PSObject.Properties[$FieldName]
    if ($null -eq $offsetProperty) { return 'unknown-field' }

    $existingValidation = if ($null -ne $Table.fieldValidation) {
        $Table.fieldValidation.PSObject.Properties[$FieldName]
    } else { $null }
    if ($null -ne $existingValidation -and [string]$existingValidation.Value.status -eq 'Stale') {
        return 'stale'
    }

    $currentOffset = [long]$offsetProperty.Value
    if ($currentOffset -ne 0 -and $currentOffset -ne $Offset) {
        return 'conflict'
    }

    if ($currentOffset -eq 0) {
        $Table.offsets.$FieldName = $Offset
    }

    if (-not ($Table.PSObject.Properties['fieldValidation']) -or
        $null -eq $Table.fieldValidation) {
        $Table | Add-Member -NotePropertyName 'fieldValidation' -NotePropertyValue ([pscustomobject]@{}) -Force
    }

    $validation = Ensure-ValidationEntry $Table $FieldName
    if ($null -eq $validation.evidence) { $validation.evidence = @() }

    $signature = "DynamicScan|0x$($Offset.ToString('X'))"
    $duplicate = @($validation.evidence | Where-Object {
        $_.provenanceKind -eq 'DynamicScan' -and $_.notes -like "*$signature*"
    }).Count -gt 0
    $evidenceAdded = $false
    if (-not $duplicate) {
        $validation.evidence = @($validation.evidence) + @([pscustomobject]@{
            provenanceKind = 'DynamicScan'
            sourceTool = 'Cheat Engine 7.7 — multiscan.lua'
            notes = "$signature; $Notes"
        })
        $evidenceAdded = $true
    }

    # Discovery must not downgrade an existing declaration.
    if ([string]$validation.status -notin @('Verified', 'Stale')) {
        $validation.status = 'Candidate'
    }

    if ($currentOffset -eq $Offset -and -not $evidenceAdded) { return 'unchanged' }
    return 'changed'
}

function Convert-ExistingOffset {
    param(
        [object]$Value,
        [string]$FieldName,
        [string]$Path
    )

    $numericTypes = @(
        [byte], [sbyte], [uint16], [int16], [uint32], [int],
        [uint64], [long], [single], [double], [decimal]
    )
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool] -or
        $Value -is [array] -or $numericTypes -notcontains $Value.GetType()) {
        throw "Offset file has a non-numeric offset for '$FieldName': $Path"
    }

    try { $decimal = [decimal]$Value } catch {
        throw "Offset file has an invalid offset for '$FieldName': $Path"
    }
    if ($decimal -ne [decimal]::Truncate($decimal) -or
        $decimal -lt 0 -or $decimal -gt 0x7FFFFFFF) {
        throw "Offset file has an out-of-range or non-integral offset for '$FieldName': $Path"
    }
    return [long]$decimal
}

function Assert-ExistingOffsetTable {
    param(
        [object]$Table,
        [string]$ExpectedVersion,
        [string]$ExpectedHash,
        [string]$Path
    )

    if ($null -eq $Table -or $Table -is [array] -or $Table -is [string]) {
        throw "Offset file root must be a JSON object: $Path"
    }
    $schemaVersionProperty = $Table.PSObject.Properties['schemaVersion']
    if ($null -eq $schemaVersionProperty -or
        $schemaVersionProperty.Value -is [string] -or $schemaVersionProperty.Value -is [bool] -or
        $schemaVersionProperty.Value -is [array] -or
        $schemaVersionProperty.Value -isnot [byte] -and $schemaVersionProperty.Value -isnot [int16] -and
        $schemaVersionProperty.Value -isnot [int32] -and $schemaVersionProperty.Value -isnot [int64] -and
        $schemaVersionProperty.Value -isnot [uint16] -and $schemaVersionProperty.Value -isnot [uint32] -and
        $schemaVersionProperty.Value -isnot [uint64] -or [decimal]$schemaVersionProperty.Value -ne 1) {
        throw "Offset file has unsupported schemaVersion: $Path"
    }
    if ($null -eq $Table.PSObject.Properties['gameVersion'] -or
        [string]$Table.gameVersion -cne $ExpectedVersion) {
        throw "Offset file version '$($Table.gameVersion)' does not match requested version '$ExpectedVersion': $Path"
    }

    $hashProperty = $Table.PSObject.Properties['executableSha256']
    if ($null -ne $hashProperty -and $null -ne $hashProperty.Value -and
        $hashProperty.Value -isnot [string]) {
        throw "Offset file executableSha256 must be a string: $Path"
    }
    $existingHash = if ($null -eq $hashProperty -or $null -eq $hashProperty.Value) {
        ''
    } else { [string]$hashProperty.Value }
    if ($existingHash -and ($existingHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $existingHash -ine $ExpectedHash)) {
        throw "Offset file executableSha256 does not match the local executable: $Path"
    }

    if ($null -eq $Table.offsets -or $Table.offsets -is [array] -or
        $Table.offsets -is [string]) {
        throw "Offset file has no valid offsets object: $Path"
    }
    foreach ($field in $KnownOffsetFields) {
        $property = $Table.offsets.PSObject.Properties[$field]
        if ($null -eq $property) {
            throw "Offset file is missing required field '$field': $Path"
        }
        Convert-ExistingOffset $property.Value $field $Path | Out-Null
    }

    $validationProperty = $Table.PSObject.Properties['fieldValidation']
    if ($null -eq $validationProperty) {
        $Table | Add-Member -NotePropertyName 'fieldValidation' -NotePropertyValue ([pscustomobject]@{}) -Force
        return
    }
    $validationObject = $validationProperty.Value
    if ($null -eq $validationObject -or $validationObject -is [array] -or
        $validationObject -is [string]) {
        throw "Offset file fieldValidation must be an object: $Path"
    }
    foreach ($property in $validationObject.PSObject.Properties) {
        if ($property.Name -notin $KnownOffsetFields) {
            throw "Offset file has unknown fieldValidation entry '$($property.Name)': $Path"
        }
        $entry = $property.Value
        if ($null -eq $entry -or $entry -is [array] -or $entry -is [string]) {
            throw "Offset file has malformed validation entry '$($property.Name)': $Path"
        }
        $statusProperty = $entry.PSObject.Properties['status']
        $evidenceProperty = $entry.PSObject.Properties['evidence']
        $status = if ($null -ne $statusProperty) { [string]$statusProperty.Value } else { '' }
        if ($status -notin @('Unknown', 'Candidate', 'Verified', 'Stale') -or
            $null -eq $evidenceProperty -or $evidenceProperty.Value -isnot [array]) {
            throw "Offset file has malformed validation entry '$($property.Name)': $Path"
        }

        $fieldOffset = Convert-ExistingOffset $Table.offsets.PSObject.Properties[$property.Name].Value $property.Name $Path
        $counterNames = @('independentProcessLaunches', 'independentReplays')
        foreach ($counterName in $counterNames) {
            $counterProperty = $entry.PSObject.Properties[$counterName]
            if ($null -ne $counterProperty -and
                ($counterProperty.Value -is [string] -or $counterProperty.Value -is [bool] -or
                 $counterProperty.Value -is [array] -or $counterProperty.Value -isnot [int] -and
                 $counterProperty.Value -isnot [long] -and $counterProperty.Value -isnot [int32] -and
                 $counterProperty.Value -isnot [int64] -or [int64]$counterProperty.Value -lt 0)) {
                throw "Offset file has an invalid $counterName value for '$($property.Name)': $Path"
            }
        }
        foreach ($booleanName in @('harnessInvariantsPassed', 'leadApproved', 'decoderAuditorApproved')) {
            $booleanProperty = $entry.PSObject.Properties[$booleanName]
            if ($null -ne $booleanProperty -and $booleanProperty.Value -isnot [bool]) {
                throw "Offset file has an invalid $booleanName value for '$($property.Name)': $Path"
            }
        }

        foreach ($evidence in @($evidenceProperty.Value)) {
            if ($null -eq $evidence -or $evidence -is [array] -or $evidence -is [string] -or
                $evidence.provenanceKind -notin @('StaticAnalysis', 'DynamicScan', 'GameHarness', 'ManualVerification') -or
                [string]::IsNullOrWhiteSpace([string]$evidence.sourceTool)) {
                throw "Offset file has malformed evidence for '$($property.Name)': $Path"
            }
        }
        if ($status -eq 'Verified') {
            $required = @('independentProcessLaunches', 'independentReplays',
                'harnessInvariantsPassed', 'leadApproved', 'decoderAuditorApproved')
            foreach ($requiredName in $required) {
                if ($null -eq $entry.PSObject.Properties[$requiredName]) {
                    throw "Verified entry is missing '$requiredName' for '$($property.Name)': $Path"
                }
            }
            $hasStatic = @($evidenceProperty.Value | Where-Object { $_.provenanceKind -eq 'StaticAnalysis' }).Count -gt 0
            $hasHarness = @($evidenceProperty.Value | Where-Object { $_.provenanceKind -eq 'GameHarness' }).Count -gt 0
            if ($fieldOffset -eq 0 -or [int64]$entry.independentProcessLaunches -lt 2 -or
                [int64]$entry.independentReplays -lt 2 -or -not [bool]$entry.harnessInvariantsPassed -or
                -not [bool]$entry.leadApproved -or -not [bool]$entry.decoderAuditorApproved -or
                -not $hasStatic -or -not $hasHarness) {
                throw "Verified entry does not contain complete evidence for '$($property.Name)': $Path"
            }
        }
    }
}

function New-OffsetTable {
    param(
        [string]$Version,
        [string]$Hash
    )

    return [pscustomobject]@{
        schemaVersion = 1
        gameVersion = $Version
        executableSha256 = $Hash
        discoveredAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        offsets = [pscustomobject]@{
            replayTime = 0
            playerHP = 0
            playerPositionX = 0
            playerPositionY = 0
            playerPositionZ = 0
            playerYaw = 0
            cameraPitch = 0
            aliveTankCount = 0
        }
        fieldValidation = [pscustomobject]@{}
        confidence = 'none'
        notes = 'Generated by the offset discovery pipeline; candidates require independent verification before promotion.'
    }
}

function Assert-SelfTest {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Self-test failed: $Message" }
}

function Invoke-DiscoverySelfTest {
    $auto = [pscustomobject]@{
        moduleName = 'wotblitz.exe'
        moduleBase = '0x10000000'
        moduleSize = 0x1000000
        fieldResults = [pscustomobject]@{
            playerYaw = [pscustomobject]@{
                candidates = @([pscustomobject]@{
                    absoluteAddress = '0x10001000'
                    relativeOffset = '0x1000'
                })
                totalCandidates = 1
            }
            playerHP = [pscustomobject]@{
                candidates = @(
                    [pscustomobject]@{ absoluteAddress = '0x10002000'; relativeOffsetDecimal = 8192 }
                    [pscustomobject]@{ absoluteAddress = '0x10003000'; relativeOffset = '0x3000' }
                )
                totalCandidates = 2
            }
        }
    }
    $autoBatches = @(Get-DiscoveryBatches $auto)
    Assert-SelfTest ($autoBatches.Count -eq 2) 'autoDiscover fieldResults shape'
    Assert-SelfTest ($autoBatches[0].FieldName -eq 'playerYaw') 'autoDiscover field name'
    $ambiguousNormalized = @(Get-NormalizedCandidates $autoBatches[1])
    Assert-SelfTest ($ambiguousNormalized.Count -eq 2) 'ambiguous normalization retains both valid candidates'
    Assert-SelfTest (@($ambiguousNormalized | Where-Object { $_.AddressKind -eq 'module-rva' }).Count -eq 2) 'module identity classification'

    $legacy = [pscustomobject]@{
        fieldName = 'cameraPitch'
        candidates = @([pscustomobject]@{ relativeOffsetDecimal = $null; relativeOffset = '0x4000' })
    }
    $legacyBatches = @(Get-DiscoveryBatches $legacy)
    Assert-SelfTest ($legacyBatches.Count -eq 1 -and $legacyBatches[0].FieldName -eq 'cameraPitch') 'legacy shape'

    $consistent = [pscustomobject]@{
        relativeOffsetDecimal = 16384
        relativeOffset = '0x4000'
    }
    Assert-SelfTest ((Convert-CandidateOffset $consistent) -eq 0x4000) 'decimal/hex agreement'
    $inconsistent = [pscustomobject]@{
        relativeOffsetDecimal = 16385
        relativeOffset = '0x4000'
    }
    Assert-SelfTest ($null -eq (Convert-CandidateOffset $inconsistent)) 'decimal/hex mismatch rejection'

    $table = New-OffsetTable '11.19.0.10' ('a' * 64)
    $first = Publish-DynamicCandidate $table 'playerYaw' 0x5000 'synthetic'
    $second = Publish-DynamicCandidate $table 'playerYaw' 0x5000 'synthetic repeat'
    Assert-SelfTest ($first -eq 'changed' -and $second -eq 'unchanged') 'same-offset publication'
    Assert-SelfTest (@($table.fieldValidation.playerYaw.evidence).Count -eq 1) 'DynamicScan evidence de-duplication'

    $table.offsets.playerHP = 0x6000
    $table.fieldValidation | Add-Member -NotePropertyName 'playerHP' -NotePropertyValue ([pscustomobject]@{
        status = 'Verified'; evidence = @([pscustomobject]@{
            provenanceKind = 'GameHarness'; sourceTool = 'synthetic'
        })
    }) -Force
    $conflictBefore = $table | ConvertTo-Json -Depth 8
    $conflict = Publish-DynamicCandidate $table 'playerHP' 0x7000 'synthetic conflict'
    $conflictAfter = $table | ConvertTo-Json -Depth 8
    Assert-SelfTest ($conflict -eq 'conflict' -and $conflictBefore -eq $conflictAfter -and
        $table.offsets.playerHP -eq 0x6000 -and
        $table.fieldValidation.playerHP.status -eq 'Verified') 'Verified conflict preservation'

    $ambiguousTable = New-OffsetTable '11.19.0.10' ('a' * 64)
    $ambiguousTable.discoveredAtUtc = '2026-07-31T00:00:00Z'
    $ambiguousBefore = $ambiguousTable | ConvertTo-Json -Depth 8
    $ambiguousPublished = 0
    if ($ambiguousNormalized.Count -eq 1) {
        $ambiguousPublished++
        [void](Publish-DynamicCandidate $ambiguousTable 'playerHP' $ambiguousNormalized[0].Offset 'unexpected synthetic publication')
    }
    if ($ambiguousPublished -gt 0) {
        $ambiguousTable.discoveredAtUtc = '2026-07-31T00:00:01Z'
    }
    $ambiguousAfter = $ambiguousTable | ConvertTo-Json -Depth 8
    Assert-SelfTest ($ambiguousNormalized.Count -ne 1 -and $ambiguousPublished -eq 0 -and
        $ambiguousBefore -eq $ambiguousAfter -and $ambiguousTable.offsets.playerHP -eq 0) 'ambiguous candidates are not publishable'

    $legacyCandidate = @(Get-NormalizedCandidates $legacyBatches[0])
    $legacyTable = New-OffsetTable '11.19.0.10' ('a' * 64)
    $legacyResult = Publish-DynamicCandidate $legacyTable 'cameraPitch' $legacyCandidate[0].Offset 'legacy synthetic'
    Assert-SelfTest ($legacyResult -eq 'changed' -and $legacyTable.offsets.cameraPitch -eq 0x4000) 'legacy candidate normalization'

    $staleTable = New-OffsetTable '11.19.0.10' ('a' * 64)
    $staleTable.fieldValidation | Add-Member -NotePropertyName 'playerYaw' -NotePropertyValue ([pscustomobject]@{
        status = 'Stale'; evidence = @()
    }) -Force
    $staleResult = Publish-DynamicCandidate $staleTable 'playerYaw' 0x5000 'stale synthetic'
    Assert-SelfTest ($staleResult -eq 'stale' -and $staleTable.offsets.playerYaw -eq 0) 'stale candidate remains quarantined'

    $legacyModuleRejected = $false
    try {
        if (-not (Test-ModuleRelativeCandidate $legacyBatches[0].Candidates[0] $legacyBatches[0])) {
            $legacyModuleRejected = $true
        }
    } catch { $legacyModuleRejected = $true }
    Assert-SelfTest $legacyModuleRejected 'legacy output without module identity is report-only'

    $mismatch = New-OffsetTable '11.18.0.7' ('a' * 64)
    $mismatchFailed = $false
    try { Assert-ExistingOffsetTable $mismatch '11.19.0.10' ('a' * 64) 'synthetic-version.json' } catch { $mismatchFailed = $true }
    Assert-SelfTest $mismatchFailed 'table version mismatch fails closed'
    $requestedMismatchFailed = $false
    try { Assert-RequestedGameVersion '11.18.0.7' '11.19.0.10' } catch { $requestedMismatchFailed = $true }
    Assert-SelfTest $requestedMismatchFailed 'requested executable version mismatch fails closed'

    $malformed = New-OffsetTable '11.19.0.10' ('a' * 64)
    $malformed.offsets.playerHP = '8192'
    $malformedFailed = $false
    try { Assert-ExistingOffsetTable $malformed '11.19.0.10' ('a' * 64) 'synthetic-malformed.json' } catch { $malformedFailed = $true }
    Assert-SelfTest $malformedFailed 'string offset fails closed'

    $hashMismatch = New-OffsetTable '11.19.0.10' ('b' * 64)
    $hashMismatchFailed = $false
    try { Assert-ExistingOffsetTable $hashMismatch '11.19.0.10' ('a' * 64) 'synthetic-hash.json' } catch { $hashMismatchFailed = $true }
    Assert-SelfTest $hashMismatchFailed 'executable hash mismatch fails closed'
    $upperHash = New-OffsetTable '11.19.0.10' ('A' * 64)
    Assert-ExistingOffsetTable $upperHash '11.19.0.10' ('a' * 64) 'synthetic-uppercase-hash.json'

    $negative = New-OffsetTable '11.19.0.10' ('a' * 64)
    $negative.offsets.playerHP = -1
    $negativeFailed = $false
    try { Assert-ExistingOffsetTable $negative '11.19.0.10' ('a' * 64) 'synthetic-negative.json' } catch { $negativeFailed = $true }
    Assert-SelfTest $negativeFailed 'negative offset fails closed'

    $badValidation = New-OffsetTable '11.19.0.10' ('a' * 64)
    $badValidation | Add-Member -NotePropertyName 'fieldValidation' -NotePropertyValue 'not-an-object' -Force
    $badValidationFailed = $false
    try { Assert-ExistingOffsetTable $badValidation '11.19.0.10' ('a' * 64) 'synthetic-validation.json' } catch { $badValidationFailed = $true }
    Assert-SelfTest $badValidationFailed 'malformed fieldValidation fails closed'
    Write-Host '[PASS] discovery self-tests: CE shapes, ambiguity, conflicts, deduplication, hash/version checks, and table validation' -ForegroundColor Green
}

function Test-Prerequisites {
    $gameProcess = Get-Process wotblitz -ErrorAction SilentlyContinue
    if ($null -eq $gameProcess) {
        Write-Host '[FAIL] wotblitz.exe is not running. Start an offline replay first.' -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] wotblitz.exe PID $($gameProcess.Id)" -ForegroundColor Green

    if ([string]::IsNullOrWhiteSpace($CeExePath) -or -not (Test-Path $CeExePath -PathType Leaf)) {
        Write-Host '[FAIL] Cheat Engine 7.5+ was not found.' -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Cheat Engine: $CeExePath" -ForegroundColor Green

    $game = Get-GameExe
    if ($null -eq $game) {
        Write-Host '[FAIL] wotblitz.exe was not found at a known install path.' -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Game executable: $($game.Path) (v$($game.Version))" -ForegroundColor Green

    if (-not (Test-Path $LuaScript -PathType Leaf)) {
        Write-Host "[FAIL] Lua script not found: $LuaScript" -ForegroundColor Red
        return $false
    }
    Write-Host "[PASS] Lua script: $LuaScript" -ForegroundColor Green
    return $true
}

Write-Host '=== WoT Blitz Offset Discovery Pipeline ===' -ForegroundColor Cyan
if ($SelfTest) {
    Invoke-DiscoverySelfTest
    exit 0
}
if ([string]::IsNullOrWhiteSpace($CeExePath)) { $CeExePath = Get-CheatEnginePath }
if ([string]::IsNullOrWhiteSpace($LuaScript)) { $LuaScript = Join-Path $RepoRoot 'tools\cheat-engine\multiscan.lua' }
if (-not (Test-Prerequisites)) { exit 1 }
if ($DryRun) {
    Write-Host 'Dry run complete; no discovery files were read or modified.' -ForegroundColor Green
    exit 0
}

$game = Get-GameExe
$detectedVersion = [string]$game.Version
Assert-RequestedGameVersion $GameVersion $detectedVersion
$version = if ([string]::IsNullOrWhiteSpace($GameVersion)) { $detectedVersion } else { $GameVersion }
$offsetFile = Join-Path $RepoRoot "memory-offsets\$version.json"
$outputFile = Join-Path $RepoRoot 'tools\cheat-engine\discovered-offsets-multiscan.json'

Write-Host ''
Write-Host 'ACTION REQUIRED:' -ForegroundColor Yellow
Write-Host '  1. Attach Cheat Engine to wotblitz.exe during an offline replay.'
Write-Host '  2. Load multiscan.lua and run autoDiscover("playerPositionX") (or another explicitly selected field).'
Write-Host "  3. Wait for $outputFile to be written."
Write-Host '  4. Do not run saveDiscovered() afterward; it is the legacy shape.'
Read-Host 'Press ENTER after the CE report exists'

if (-not (Test-Path $outputFile -PathType Leaf)) {
    throw "Cheat Engine output not found: $outputFile"
}

$data = Get-Content $outputFile -Raw | ConvertFrom-Json
$batches = @(Get-DiscoveryBatches $data)
if ($batches.Count -eq 0) {
    throw 'CE output has neither fieldResults nor legacy fieldName/candidates data.'
}
$normalized = @()
foreach ($batch in $batches) {
    $valid = @(Get-NormalizedCandidates $batch)
    $normalized += $valid
    $state = if ($valid.Count -eq 1 -and @($batch.Candidates).Count -eq 1 -and $batch.ReportedCandidateCount -eq 1 -and $valid[0].AddressKind -eq 'module-rva') { 'unique module candidate' } else { 'report-only' }
    Write-Host "  $($batch.FieldName): $(@($batch.Candidates).Count) raw, $($valid.Count) valid, $state"
}

$hash = (Get-FileHash -Path $game.Path -Algorithm SHA256).Hash.ToLowerInvariant()
$offsetFileExisted = Test-Path $offsetFile -PathType Leaf
$table = if ($offsetFileExisted) {
    try {
        Get-Content $offsetFile -Raw | ConvertFrom-Json
    } catch {
        throw "Offset file is not valid JSON: $offsetFile"
    }
} else {
    New-OffsetTable $version $hash
}
if ($offsetFileExisted) {
    Assert-ExistingOffsetTable $table $version $hash $offsetFile
}

$published = 0
$conflicts = 0
foreach ($batch in $batches) {
    $valid = @($normalized | Where-Object { $_.FieldName -eq $batch.FieldName })
    if ($valid.Count -ne 1 -or @($batch.Candidates).Count -ne 1 -or
        $batch.ReportedCandidateCount -ne 1 -or $valid[0].AddressKind -ne 'module-rva') {
        continue
    }

    $candidate = $valid[0]
    $result = Publish-DynamicCandidate $table $batch.FieldName $candidate.Offset "Unique candidate from $($valid.Count) valid candidate(s); independent process/replay verification remains required."
    switch ($result) {
        'changed' { $published++ }
        'unchanged' { }
        'conflict' {
            $conflicts++
            Write-Host "  $($batch.FieldName): conflict; existing offset retained" -ForegroundColor Yellow
        }
        'stale' {
            $conflicts++
            Write-Host "  $($batch.FieldName): stale/quarantined evidence retained; explicit reconciliation required" -ForegroundColor Yellow
        }
        'unknown-field' {
            Write-Host "  $($batch.FieldName): unknown contract field; ignored" -ForegroundColor Yellow
        }
    }
}

if ($published -gt 0) {
    $table.executableSha256 = $hash
    $table.discoveredAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    if ([string]$table.confidence -eq 'none') { $table.confidence = 'low' }
    $table | ConvertTo-Json -Depth 8 | Set-Content $offsetFile -Encoding UTF8
    Write-Host "Updated ${offsetFile}: $published uniquely publishable, $conflicts conflicting." -ForegroundColor Green
} else {
    Write-Host "Report-only: no offset evidence changed; $conflicts conflicting." -ForegroundColor Yellow
}

if (-not $SkipValidation) {
    $checker = Join-Path $RepoRoot 'scripts\python\offset_check.py'
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $python -and (Test-Path $checker -PathType Leaf)) {
        & $python.Source $checker
    } else {
        Write-Host '[WARN] Python validator unavailable; skipped.' -ForegroundColor Yellow
    }
}
