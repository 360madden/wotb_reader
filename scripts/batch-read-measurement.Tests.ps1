# Pester smoke tests for Item-7 batch read-pass measurement evidence.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'batch-read-measurement.ps1')
. (Join-Path $here 'batch-rehearsal-support.ps1')

function New-SyntheticBatchResponse {
    param(
        [object] $Started = '2026-08-14T20:00:00.0000000+00:00',
        [object] $Ended = '2026-08-14T20:00:00.0120000+00:00',
        [object] $Clock = '2026-08-14T20:00:00.0150000+00:00'
    )

    return [pscustomobject]@{
        status      = 'Resolved'
        regions     = @([pscustomobject]@{ entityId = 42; regionBase64 = 'AA==' })
        measurement = [pscustomobject]@{
            batchStartedAtUtc  = $Started
            batchEndedAtUtc    = $Ended
            clockSnapshotAtUtc = $Clock
        }
    }
}

Describe 'batch read-pass measurement evidence' {
    It 'copies the bounded timestamps and derives the two windows' {
        $evidence = ConvertTo-BatchReadMeasurementEvidence `
            -Response (New-SyntheticBatchResponse)

        $evidence.readPassMilliseconds | Should Be 12
        $evidence.clockSnapshotLagMilliseconds | Should Be 3
        $evidence.batchStartedAtUtc | Should Be '2026-08-14T20:00:00.0000000+00:00'
        $evidence.Keys.Count | Should Be 5
    }

    It 'fails closed when the endpoint measurement is absent' {
        $response = [pscustomobject]@{ status = 'Resolved' }

        { ConvertTo-BatchReadMeasurementEvidence -Response $response } |
            Should Throw
    }

    It 'fails closed when the read pass is reversed' {
        $response = New-SyntheticBatchResponse `
            -Started '2026-08-14T20:00:00.0200000+00:00' `
            -Ended '2026-08-14T20:00:00.0100000+00:00'

        { ConvertTo-BatchReadMeasurementEvidence -Response $response } |
            Should Throw
    }

    It 'fails closed when the clock snapshot predates the read pass end' {
        $response = New-SyntheticBatchResponse `
            -Ended '2026-08-14T20:00:00.0120000+00:00' `
            -Clock '2026-08-14T20:00:00.0110000+00:00'

        { ConvertTo-BatchReadMeasurementEvidence -Response $response } |
            Should Throw
    }
}

function New-SyntheticBatchRegion {
    param(
        [string] $Status = 'Resolved',
        [bool] $Consistent = $true,
        [int] $RegionAttempts = 1,
        [bool] $RegionTear = $false,
        [int] $EntityBaseAttempts = 0,
        [bool] $EntityBaseTear = $false
    )

    return [pscustomobject]@{
        entityId                  = 42
        status                    = $Status
        consistentDoubleRead      = $Consistent
        regionReadAttempts        = $RegionAttempts
        regionTearObserved        = $RegionTear
        entityBaseAttempts        = $EntityBaseAttempts
        entityBaseTearObserved    = $EntityBaseTear
        regionBase64              = 'AA=='
    }
}

Describe 'batch rehearsal live support' {
    It 'retries the transient rendezvous replacement window' {
        $script:rendezvousAttempts = 0
        $candidateReader = {
            $script:rendezvousAttempts++
            if ($script:rendezvousAttempts -lt 3) { return $null }
            return [pscustomobject]@{
                Value = [pscustomobject]@{
                    baseUri   = 'http://127.0.0.1:9182/'
                    capability = 'synthetic'
                }
                LastWriteTimeUtc = [DateTime]::UtcNow
            }
        }

        $result = Get-RehearsalRendezvous -MaxAttempts 3 `
            -DelayMilliseconds 0 -CandidateReader $candidateReader

        $script:rendezvousAttempts | Should Be 3
        $result.baseUri | Should Be 'http://127.0.0.1:9182/'
    }

    It 'rejects a non-loopback rendezvous' {
        $candidateReader = {
            [pscustomobject]@{
                Value = [pscustomobject]@{
                    baseUri   = 'https://example.invalid/'
                    capability = 'synthetic'
                }
                LastWriteTimeUtc = [DateTime]::UtcNow
            }
        }

        Get-RehearsalRendezvous -MaxAttempts 1 -DelayMilliseconds 0 `
            -CandidateReader $candidateReader | Should Be $null
    }

    It 'summarizes the post-contract witness without sensitive fields' {
        $evidence = ConvertTo-BatchReadWitnessEvidence -Regions @(
            (New-SyntheticBatchRegion),
            (New-SyntheticBatchRegion -RegionAttempts 2 -RegionTear $true `
                -EntityBaseAttempts 2 -EntityBaseTear $true))

        $evidence.regions | Should Be 2
        $evidence.resolved | Should Be 2
        $evidence.consistentDoubleReads | Should Be 2
        $evidence.regionTearsObserved | Should Be 1
        $evidence.entityBaseTearsObserved | Should Be 1
        $evidence.maxRegionReadAttempts | Should Be 2
        $evidence.Keys -contains 'entityId' | Should Be $false
        $evidence.Keys -contains 'regionBase64' | Should Be $false
    }

    It 'fails closed when the new witness fields are missing' {
        $oldHostRegion = [pscustomobject]@{
            status               = 'Resolved'
            consistentDoubleRead = $false
        }

        { ConvertTo-BatchReadWitnessEvidence -Regions @($oldHostRegion) } |
            Should Throw
    }

    It 'fails closed when a resolved item lacks the stable witness' {
        $region = New-SyntheticBatchRegion -Consistent $false

        { ConvertTo-BatchReadWitnessEvidence -Regions @($region) } |
            Should Throw
    }
}
