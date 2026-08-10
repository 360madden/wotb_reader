<#
.SYNOPSIS
    Reports the evidence status of versioned WoT Blitz offset tables.

.DESCRIPTION
    Read-only companion to the discovery pipeline. It loads one or all
    memory-offsets/<version>.json files and reports the executable hash state,
    per-field Unknown/Candidate/Verified/Stale status, provenance kinds, and
    whether the field has enough evidence for runtime promotion.

    This script never edits offset files, scanner state, or discovery results.

.PARAMETER GameVersion
    Optional version to report, for example 11.19.0.10. When omitted, all
    versioned offset files are reported.

.PARAMETER OffsetDir
    Optional memory-offsets directory. Defaults to the repository directory.

.EXAMPLE
    .\tools\report-offset-evidence.ps1

.EXAMPLE
    .\tools\report-offset-evidence.ps1 -GameVersion 11.19.0.10
#>

[CmdletBinding()]
param(
    [string]$GameVersion,
    [string]$OffsetDir,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Get-Item $PSScriptRoot).Parent.FullName
if (-not $OffsetDir) { $OffsetDir = Join-Path $RepoRoot 'memory-offsets' }

$knownFields = @(
    'replayTime', 'playerHP', 'playerPositionX', 'playerPositionY',
    'playerPositionZ', 'playerYaw', 'cameraPitch', 'aliveTankCount'
)

function Get-FieldStatus {
    param(
        [object]$OffsetObject,
        [object]$ValidationObject
    )

    try {
        if ($null -eq $OffsetObject -or $OffsetObject -is [string] -or
            $OffsetObject -is [bool] -or $OffsetObject -is [array]) {
            return 'Invalid'
        }
        $decimal = [decimal]$OffsetObject
        if ($decimal -ne [decimal]::Truncate($decimal) -or
            $decimal -lt 0 -or $decimal -gt 0x7FFFFFFF) {
            return 'Invalid'
        }
        # Chained fields (see the `chains` section) keep their offsets value 0
        # by design; their status comes from fieldValidation. Only plain offset-0
        # fields are Unknown.
        $isChainedVerified = $decimal -eq 0 -and
            $null -ne $ValidationObject -and
            [string]$ValidationObject.status -eq 'Verified'
        if ($decimal -eq 0 -and -not $isChainedVerified) { return 'Unknown' }
    } catch {
        return 'Invalid'
    }
    if ($null -eq $ValidationObject) { return 'Candidate' }
    if ([string]$ValidationObject.status -eq 'Stale') { return 'Stale' }

    $evidence = @($ValidationObject.evidence)
    $hasStatic = @($evidence | Where-Object { $_.provenanceKind -eq 'StaticAnalysis' }).Count -gt 0
    $hasHarness = @($evidence | Where-Object { $_.provenanceKind -eq 'GameHarness' }).Count -gt 0
    $complete = [int]$ValidationObject.independentProcessLaunches -ge 2 -and
        [int]$ValidationObject.independentReplays -ge 2 -and
        [bool]$ValidationObject.harnessInvariantsPassed -and
        [bool]$ValidationObject.leadApproved -and
        [bool]$ValidationObject.decoderAuditorApproved -and
        $hasStatic -and $hasHarness

    if ([string]$ValidationObject.status -eq 'Verified' -and $complete) { return 'Verified' }
    return 'Candidate'
}

function Assert-ReportShape {
    param([object]$Data)

    if ($null -eq $Data.offsets -or $Data.offsets -is [array] -or
        $Data.offsets -is [string]) {
        throw 'offsets must be an object'
    }
    $offsetProperties = @($Data.offsets.PSObject.Properties)
    foreach ($field in $knownFields) {
        $offsetProperty = $Data.offsets.PSObject.Properties[$field]
        if ($null -eq $offsetProperty) {
            throw "missing required offset '$field'"
        }
        $status = Get-FieldStatus $offsetProperty.Value $null
        if ($status -eq 'Invalid') {
            throw "invalid offset value for '$field'"
        }
    }
    foreach ($property in $offsetProperties) {
        if ($property.Name -notin $knownFields) {
            throw "unknown offsets entry '$($property.Name)'"
        }
    }
    if ($null -eq $Data.fieldValidation) { return }
    if ($Data.fieldValidation -is [array] -or $Data.fieldValidation -is [string]) {
        throw 'fieldValidation must be an object'
    }
    foreach ($property in $Data.fieldValidation.PSObject.Properties) {
        if ($property.Name -notin $knownFields) { throw "unknown fieldValidation entry '$($property.Name)'" }
        $entry = $property.Value
        if ($null -eq $entry -or $entry -is [array] -or $entry -is [string]) {
            throw "malformed validation entry '$($property.Name)'"
        }
        $statusProperty = $entry.PSObject.Properties['status']
        $evidenceProperty = $entry.PSObject.Properties['evidence']
        if ($null -eq $statusProperty -or
            [string]$statusProperty.Value -notin @('Unknown', 'Candidate', 'Verified', 'Stale') -or
            $null -eq $evidenceProperty -or $evidenceProperty.Value -isnot [array]) {
            throw "malformed validation evidence for '$($property.Name)'"
        }
        foreach ($evidence in @($evidenceProperty.Value)) {
            if ($null -eq $evidence -or $evidence -is [array] -or $evidence -is [string] -or
                $evidence.provenanceKind -notin @('StaticAnalysis', 'DynamicScan', 'GameHarness', 'ManualVerification') -or
                [string]::IsNullOrWhiteSpace([string]$evidence.sourceTool)) {
                throw "malformed evidence item for '$($property.Name)'"
            }
        }
        if ([string]$statusProperty.Value -eq 'Verified') {
            $required = @('independentProcessLaunches', 'independentReplays',
                'harnessInvariantsPassed', 'leadApproved', 'decoderAuditorApproved')
            foreach ($requiredName in $required) {
                $requiredProperty = $entry.PSObject.Properties[$requiredName]
                if ($null -eq $requiredProperty) {
                    throw "Verified entry is missing '$requiredName' for '$($property.Name)'"
                }
            }
            $hasStatic = @($evidenceProperty.Value | Where-Object { $_.provenanceKind -eq 'StaticAnalysis' }).Count -gt 0
            $hasHarness = @($evidenceProperty.Value | Where-Object { $_.provenanceKind -eq 'GameHarness' }).Count -gt 0
            if ([int64]$entry.independentProcessLaunches -lt 2 -or
                [int64]$entry.independentReplays -lt 2 -or
                -not [bool]$entry.harnessInvariantsPassed -or
                -not [bool]$entry.leadApproved -or
                -not [bool]$entry.decoderAuditorApproved -or
                -not $hasStatic -or -not $hasHarness) {
                throw "Verified entry has incomplete evidence for '$($property.Name)'"
            }
        }
    }
}

function Write-OffsetReport {
    param([string]$Path)

    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    try {
        $data = Get-Content $Path -Raw | ConvertFrom-Json
    } catch {
        $script:InvalidReport = $true
        Write-Host ""
        Write-Host "[$name]" -ForegroundColor Cyan
        Write-Host "  status             : Invalid (JSON parse failed)" -ForegroundColor Yellow
        Write-Host "  error              : $($_.Exception.Message)" -ForegroundColor Yellow
        return
    }
    if ($null -eq $data -or $data -is [array] -or $data -is [string]) {
        $script:InvalidReport = $true
        Write-Host ""
        Write-Host "[$name]" -ForegroundColor Cyan
        Write-Host '  status             : Invalid (root is not a JSON object)' -ForegroundColor Yellow
        return
    }
    Assert-ReportShape $data
    $hash = [string]$data.executableSha256
    $hashStatus = if ($hash -match '^[0-9a-fA-F]{64}$') { 'present' } else { 'missing-or-invalid' }

    Write-Host ""
    Write-Host "[$name]" -ForegroundColor Cyan
    Write-Host "  gameVersion       : $($data.gameVersion)"
    Write-Host "  executableSha256  : $hashStatus"
    Write-Host "  confidence        : $($data.confidence)"
    Write-Host "  discoveredAtUtc   : $($data.discoveredAtUtc)"

    $verified = 0
    $candidate = 0
    $invalid = 0
    $unknown = 0
    foreach ($field in $knownFields) {
        $offsetProperty = if ($null -ne $data.offsets) {
            $data.offsets.PSObject.Properties[$field]
        } else { $null }
        $offset = if ($null -ne $offsetProperty) { $offsetProperty.Value } else { 0 }
        $validationProperty = if ($null -ne $data.fieldValidation) {
            $data.fieldValidation.PSObject.Properties[$field]
        } else { $null }
        $validation = if ($null -ne $validationProperty) { $validationProperty.Value } else { $null }
        $status = Get-FieldStatus $offset $validation
        $provenance = if ($null -ne $validation -and $validation.evidence -is [array]) {
            (@($validation.evidence | ForEach-Object { $_.provenanceKind }) -join ',')
        } else { '' }
        $offsetText = if ($status -eq 'Invalid') { 'invalid' } elseif ([decimal]$offset -eq 0) {
            '0'
        } else { ('0x{0:X}' -f [long]$offset) }
        Write-Host ("  {0,-18} : {1,-8} {2,-12} {3}" -f $field, $status, $offsetText, $provenance)

        switch ($status) {
            'Verified' { $verified++ }
            'Candidate' { $candidate++ }
            'Invalid' { $invalid++ }
            default { $unknown++ }
        }
    }

    Write-Host "  summary            : verified=$verified candidate=$candidate invalid=$invalid unknown-or-stale=$unknown"
}

if ($SelfTest) {
    $selfTestDir = Join-Path ([IO.Path]::GetTempPath()) ('wotb-offset-report-' + [guid]::NewGuid().ToString('N'))
    New-Item -Path $selfTestDir -ItemType Directory -Force | Out-Null
    try {
        Set-Content (Join-Path $selfTestDir 'malformed-offset.json') '{"offsets":{"playerYaw":"not-a-number"}}'
        Set-Content (Join-Path $selfTestDir 'malformed-validation.json') '{"offsets":{"playerYaw":8192},"fieldValidation":{"playerYaw":{"status":"Candidate","evidence":{"provenanceKind":"DynamicScan"}}}}'
        Set-Content (Join-Path $selfTestDir 'malformed-evidence-item.json') '{"offsets":{"playerYaw":8192},"fieldValidation":{"playerYaw":{"status":"Candidate","evidence":[{"provenanceKind":"Unknown","sourceTool":""}]}}}'
        Set-Content (Join-Path $selfTestDir 'incomplete-verified.json') '{"offsets":{"playerYaw":8192},"fieldValidation":{"playerYaw":{"status":"Verified","evidence":[{"provenanceKind":"DynamicScan","sourceTool":"synthetic"}]}}}'

        $script:InvalidReport = $false
        try {
            Write-OffsetReport (Join-Path $selfTestDir 'malformed-offset.json')
        } catch {
            $script:InvalidReport = $true
        }
        if (-not $script:InvalidReport) { throw 'Self-test failed: malformed offset did not fail closed' }

        $script:InvalidReport = $false
        try {
            Write-OffsetReport (Join-Path $selfTestDir 'malformed-validation.json')
        } catch {
            $script:InvalidReport = $true
        }
        if (-not $script:InvalidReport) { throw 'Self-test failed: malformed validation did not fail closed' }

        $script:InvalidReport = $false
        try {
            Write-OffsetReport (Join-Path $selfTestDir 'malformed-evidence-item.json')
        } catch {
            $script:InvalidReport = $true
        }
        if (-not $script:InvalidReport) { throw 'Self-test failed: malformed evidence item did not fail closed' }

        $script:InvalidReport = $false
        try {
            Write-OffsetReport (Join-Path $selfTestDir 'incomplete-verified.json')
        } catch {
            $script:InvalidReport = $true
        }
        if (-not $script:InvalidReport) { throw 'Self-test failed: incomplete Verified evidence did not fail closed' }

        # Chained Verified field (offset 0, complete evidence) must report
        # 'Verified'; a plain offset-0 field stays 'Unknown'.
        $chainedValidation = [pscustomobject]@{
            status = 'Verified'
            evidence = @(
                [pscustomobject]@{ provenanceKind = 'StaticAnalysis'; sourceTool = 'synthetic' },
                [pscustomobject]@{ provenanceKind = 'GameHarness'; sourceTool = 'synthetic' }
            )
            independentProcessLaunches = 2
            independentReplays = 2
            harnessInvariantsPassed = $true
            leadApproved = $true
            decoderAuditorApproved = $true
        }
        $chainedStatus = Get-FieldStatus 0 $chainedValidation
        if ($chainedStatus -ne 'Verified') {
            throw "Self-test failed: chained Verified field (offset 0) reported as '$chainedStatus'"
        }
        $plainStatus = Get-FieldStatus 0 $null
        if ($plainStatus -ne 'Unknown') {
            throw "Self-test failed: plain offset-0 field reported as '$plainStatus'"
        }

        $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
        if ($null -eq $pwsh) { throw 'Self-test failed: pwsh is required for subprocess exit validation' }
        & $pwsh.Source -NoProfile -File $PSCommandPath -OffsetDir $selfTestDir *> $null
        if ($LASTEXITCODE -eq 0) { throw 'Self-test failed: malformed report directory returned exit code 0' }
        Write-Host '[PASS] report self-test: malformed offsets, validation, evidence items, Verified completeness, and subprocess exit are invalid' -ForegroundColor Green
    } finally {
        Remove-Item $selfTestDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

if (-not (Test-Path $OffsetDir -PathType Container)) {
    throw "Offset directory not found: $OffsetDir"
}

$files = if ($GameVersion) {
    @(Join-Path $OffsetDir "$GameVersion.json")
} else {
    @(Get-ChildItem $OffsetDir -Filter '*.json' |
        Where-Object { $_.BaseName -notin @('schema', 'scanner-state') } |
        ForEach-Object { $_.FullName })
}

if ($files.Count -eq 0) { throw "No versioned offset files found in $OffsetDir" }
$script:InvalidReport = $false
foreach ($file in $files) {
    if (-not (Test-Path $file -PathType Leaf)) { throw "Offset file not found: $file" }
    try {
        Write-OffsetReport $file
    } catch {
        $script:InvalidReport = $true
        $name = [IO.Path]::GetFileNameWithoutExtension($file)
        Write-Host ""
        Write-Host "[$name]" -ForegroundColor Cyan
        Write-Host "  status             : Invalid (malformed structure)" -ForegroundColor Yellow
        Write-Host "  error              : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if ($script:InvalidReport) { exit 1 }
