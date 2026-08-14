# Pester smoke tests for Item-7 batch read-pass measurement evidence.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'batch-read-measurement.ps1')

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
