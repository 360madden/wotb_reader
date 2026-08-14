#Requires -Version 5.1
<#
.SYNOPSIS
  Pure aggregation helpers for Item-7 Branch-B camera-pose double-read
  evidence.

.DESCRIPTION
  The camera-pose endpoint response includes process addresses and pose
  coordinates. These helpers deliberately copy only the status, failure
  stage, identity gates, module-rooted witness, and ConsistentDoubleRead
  flag needed by the hardware-atomicity evidence contract.
#>

function Get-CameraDoubleReadResponseValue {
    param(
        [Parameter(Mandatory)]
        [object] $Response,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $property = $Response.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function New-CameraDoubleReadMeasurementState {
    return @{
        ProbesCompleted        = 0
        Resolved               = 0
        IdentityVerified       = 0
        ConsistentDoubleReads  = 0
        PoseDoubleReadFailures = 0
    }
}

function Add-CameraDoubleReadMeasurement {
    param(
        [Parameter(Mandatory)]
        [hashtable] $State,

        [Parameter(Mandatory)]
        [object] $Response
    )

    $status = [string](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'status')
    $failureStage = [string](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'failureStage')
    $consistentDoubleRead = [bool](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'consistentDoubleRead')
    $avatarIdentityVerified = [bool](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'avatarIdentityVerified')
    $cameraIdentityVerified = [bool](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'cameraIdentityVerified')
    $cameraStateIdentityVerified = [bool](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'cameraStateIdentityVerified')
    $moduleRooted = [bool](Get-CameraDoubleReadResponseValue `
            -Response $Response -Name 'moduleRooted')

    $State.ProbesCompleted = [int]$State.ProbesCompleted + 1
    if ($status -eq 'Resolved') {
        $State.Resolved = [int]$State.Resolved + 1
    }
    if ($avatarIdentityVerified -and $cameraIdentityVerified -and
        $cameraStateIdentityVerified -and $moduleRooted) {
        $State.IdentityVerified = [int]$State.IdentityVerified + 1
    }
    if ($consistentDoubleRead) {
        $State.ConsistentDoubleReads = [int]$State.ConsistentDoubleReads + 1
    }
    if ($failureStage -eq 'pose-double-read') {
        $State.PoseDoubleReadFailures = [int]$State.PoseDoubleReadFailures + 1
    }

    # Privacy boundary: this whitelist intentionally excludes endpoint
    # addresses, pose coordinates, basis values, and capability material.
    return [ordered]@{
        status                      = $status
        failureStage                = $failureStage
        consistentDoubleRead        = $consistentDoubleRead
        avatarIdentityVerified      = $avatarIdentityVerified
        cameraIdentityVerified      = $cameraIdentityVerified
        cameraStateIdentityVerified = $cameraStateIdentityVerified
        moduleRooted                = $moduleRooted
    }
}

function Complete-CameraDoubleReadMeasurement {
    param(
        [Parameter(Mandatory)]
        [hashtable] $State,

        [Parameter(Mandatory)]
        [ValidateRange(1, 60)]
        [int] $ExpectedProbes
    )

    $probesCompleted = [int]$State.ProbesCompleted
    $resolved = [int]$State.Resolved
    $identityVerified = [int]$State.IdentityVerified
    $consistentDoubleReads = [int]$State.ConsistentDoubleReads
    $poseDoubleReadFailures = [int]$State.PoseDoubleReadFailures
    $allResolvedConsistent = $probesCompleted -eq $ExpectedProbes -and
        $resolved -eq $ExpectedProbes -and
        $identityVerified -eq $ExpectedProbes -and
        $consistentDoubleReads -eq $ExpectedProbes -and
        $poseDoubleReadFailures -eq 0

    return [ordered]@{
        endpoint               = '/api/v1/game/discover/camera-pose'
        expectedProbes         = $ExpectedProbes
        probesCompleted        = $probesCompleted
        resolved               = $resolved
        identityVerified       = $identityVerified
        consistentDoubleReads  = $consistentDoubleReads
        poseDoubleReadFailures = $poseDoubleReadFailures
        allResolvedConsistent  = $allResolvedConsistent
    }
}
