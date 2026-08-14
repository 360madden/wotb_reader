# Pester smoke tests for the Item-7 Branch-B camera-pose measurement helper.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'camera-double-read-measurement.ps1')

function New-SyntheticCameraPoseResponse {
    param(
        [string] $Status = 'Resolved',
        [string] $FailureStage = '',
        [bool] $ConsistentDoubleRead = $true
    )

    return [pscustomobject]@{
        status                      = $Status
        failureStage                = $FailureStage
        consistentDoubleRead        = $ConsistentDoubleRead
        avatarIdentityVerified      = $true
        cameraIdentityVerified      = $true
        cameraStateIdentityVerified = $true
        moduleRooted                = $true
        avatarAddress               = '0x11111111'
        cameraAddress               = '0x22222222'
        cameraStateAddress          = '0x33333333'
        x                            = 1.0
        y                            = 2.0
        z                            = 3.0
        basis                        = @(1.0, 0.0, 0.0)
    }
}

Describe 'camera double-read measurement' {
    It 'accepts a complete set of resolved consistent probes' {
        $state = New-CameraDoubleReadMeasurementState
        Add-CameraDoubleReadMeasurement `
            -State $state -Response (New-SyntheticCameraPoseResponse) | Out-Null
        Add-CameraDoubleReadMeasurement `
            -State $state -Response (New-SyntheticCameraPoseResponse) | Out-Null

        $summary = Complete-CameraDoubleReadMeasurement -State $state -ExpectedProbes 2

        $summary.probesCompleted | Should Be 2
        $summary.resolved | Should Be 2
        $summary.identityVerified | Should Be 2
        $summary.consistentDoubleReads | Should Be 2
        $summary.poseDoubleReadFailures | Should Be 0
        $summary.allResolvedConsistent | Should Be $true
    }

    It 'records a pose double-read mismatch as an honest negative' {
        $state = New-CameraDoubleReadMeasurementState
        $probe = Add-CameraDoubleReadMeasurement -State $state -Response (
            New-SyntheticCameraPoseResponse `
                -Status 'Unresolved' `
                -FailureStage 'pose-double-read' `
                -ConsistentDoubleRead $false)
        $summary = Complete-CameraDoubleReadMeasurement -State $state -ExpectedProbes 1

        $probe.failureStage | Should Be 'pose-double-read'
        $summary.poseDoubleReadFailures | Should Be 1
        $summary.allResolvedConsistent | Should Be $false
    }

    It 'whitelists evidence fields and excludes addresses and coordinates' {
        $state = New-CameraDoubleReadMeasurementState
        $probe = Add-CameraDoubleReadMeasurement `
            -State $state -Response (New-SyntheticCameraPoseResponse)
        $names = @($probe.Keys)

        $names.Count | Should Be 7
        ($names -contains 'consistentDoubleRead') | Should Be $true
        ($names -contains 'avatarAddress') | Should Be $false
        ($names -contains 'cameraAddress') | Should Be $false
        ($names -contains 'cameraStateAddress') | Should Be $false
        ($names -contains 'x') | Should Be $false
        ($names -contains 'basis') | Should Be $false
    }

    It 'rejects an incomplete probe schedule' {
        $state = New-CameraDoubleReadMeasurementState
        Add-CameraDoubleReadMeasurement `
            -State $state -Response (New-SyntheticCameraPoseResponse) | Out-Null

        $summary = Complete-CameraDoubleReadMeasurement -State $state -ExpectedProbes 2

        $summary.allResolvedConsistent | Should Be $false
    }
}
