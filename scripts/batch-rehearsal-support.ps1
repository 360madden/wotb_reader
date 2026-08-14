#Requires -Version 5.1
<#
.SYNOPSIS
  Bounded live-support helpers for the batch rehearsal.

.DESCRIPTION
  Recovers from the rendezvous publisher's brief replace window and validates
  the post-contract per-region double-read witness before live evidence is
  retained. The witness aggregate contains counts only: no entity ids, raw
  region bytes, addresses, paths, or capability material.
#>

function Get-RehearsalRendezvous {
    param(
        [ValidateRange(1, 20)]
        [int] $MaxAttempts = 5,

        [ValidateRange(0, 5000)]
        [int] $DelayMilliseconds = 100,

        [scriptblock] $CandidateReader
    )

    if ($null -eq $CandidateReader) {
        $CandidateReader = {
            $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
            $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1
            if ($null -eq $file) {
                return $null
            }
            return [pscustomobject]@{
                Value            = Get-Content -LiteralPath $file.FullName -Raw |
                    ConvertFrom-Json
                LastWriteTimeUtc = $file.LastWriteTimeUtc
            }
        }
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $candidate = $null
        try {
            $candidate = & $CandidateReader
        }
        catch {
            $candidate = $null
        }

        if ($null -ne $candidate -and
            $candidate.PSObject.Properties['Value'] -and
            $candidate.PSObject.Properties['LastWriteTimeUtc']) {
            $value = $candidate.Value
            $lastWrite = $candidate.LastWriteTimeUtc
            if ($null -ne $value -and
                $lastWrite -is [DateTime] -and
                $lastWrite -ge [DateTime]::UtcNow.AddMinutes(-10) -and
                $value.PSObject.Properties['baseUri'] -and
                $value.PSObject.Properties['capability'] -and
                -not [string]::IsNullOrWhiteSpace([string]$value.baseUri) -and
                -not [string]::IsNullOrWhiteSpace([string]$value.capability)) {
                try {
                    $uri = [Uri][string]$value.baseUri
                    if ($uri.IsLoopback -and $uri.Scheme -eq 'http') {
                        return $value
                    }
                }
                catch {
                    # Invalid URI: bounded loop retries and then fails closed.
                }
            }
        }

        if ($attempt -lt $MaxAttempts -and $DelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }

    return $null
}

function ConvertTo-BatchReadWitnessEvidence {
    param(
        [Parameter(Mandatory)]
        [object[]] $Regions
    )

    if ($Regions.Count -lt 1) {
        throw [InvalidOperationException]::new('batch witness has no regions')
    }

    $resolved = 0
    $consistent = 0
    $regionTears = 0
    $entityBaseReads = 0
    $entityBaseTears = 0
    $maxRegionAttempts = 0
    $maxEntityBaseAttempts = 0

    foreach ($region in $Regions) {
        foreach ($name in @(
                'status',
                'consistentDoubleRead',
                'regionReadAttempts',
                'regionTearObserved',
                'entityBaseAttempts',
                'entityBaseTearObserved')) {
            if (-not $region.PSObject.Properties[$name]) {
                throw [InvalidOperationException]::new(
                    "batch witness field is missing: $name")
            }
        }

        if ($region.consistentDoubleRead -isnot [bool] -or
            $region.regionTearObserved -isnot [bool] -or
            $region.entityBaseTearObserved -isnot [bool]) {
            throw [InvalidOperationException]::new(
                'batch witness boolean field has the wrong type')
        }

        try {
            $regionAttempts = [int]$region.regionReadAttempts
            $entityBaseAttempts = [int]$region.entityBaseAttempts
        }
        catch {
            throw [InvalidOperationException]::new(
                'batch witness attempt field has the wrong type')
        }
        if ($regionAttempts -lt 0 -or $entityBaseAttempts -lt 0) {
            throw [InvalidOperationException]::new(
                'batch witness attempt field is negative')
        }
        if ($region.regionTearObserved -and $regionAttempts -lt 2) {
            throw [InvalidOperationException]::new(
                'region tear is inconsistent with the attempt count')
        }
        if ($region.entityBaseTearObserved -and $entityBaseAttempts -lt 2) {
            throw [InvalidOperationException]::new(
                'entity-base tear is inconsistent with the attempt count')
        }

        $maxRegionAttempts = [Math]::Max($maxRegionAttempts, $regionAttempts)
        $maxEntityBaseAttempts = [Math]::Max(
            $maxEntityBaseAttempts,
            $entityBaseAttempts)
        if ($region.regionTearObserved) { $regionTears++ }
        if ($entityBaseAttempts -gt 0) { $entityBaseReads++ }
        if ($region.entityBaseTearObserved) { $entityBaseTears++ }

        if ([string]$region.status -eq 'Resolved') {
            $resolved++
            if (-not $region.consistentDoubleRead -or $regionAttempts -lt 1) {
                throw [InvalidOperationException]::new(
                    'resolved batch region lacks a stable double-read witness')
            }
            $consistent++
        }
        elseif ($region.consistentDoubleRead) {
            throw [InvalidOperationException]::new(
                'unresolved batch region claims a stable double-read witness')
        }
    }

    return [ordered]@{
        regions                    = $Regions.Count
        resolved                   = $resolved
        consistentDoubleReads      = $consistent
        regionTearsObserved        = $regionTears
        maxRegionReadAttempts      = $maxRegionAttempts
        entityBaseReads            = $entityBaseReads
        entityBaseTearsObserved    = $entityBaseTears
        maxEntityBaseReadAttempts  = $maxEntityBaseAttempts
        allResolvedConsistent      = ($resolved -eq $consistent)
    }
}
