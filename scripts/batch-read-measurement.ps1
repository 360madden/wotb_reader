#Requires -Version 5.1
<#
.SYNOPSIS
  Converts the batch endpoint's read-pass measurement into bounded Item-7
  evidence.

.DESCRIPTION
  The helper accepts only the three wall-clock timestamps already exposed by
  EntityRegionsReadMeasurementResponse. It validates their ordering and adds
  the read-pass duration plus post-read clock-snapshot lag. No raw region
  bytes, entity identifiers, addresses, or capabilities enter this evidence.
#>

function ConvertTo-BatchReadMeasurementEvidence {
    param(
        [Parameter(Mandatory)]
        [object] $Response
    )

    $measurementProperty = $Response.PSObject.Properties['measurement']
    if ($null -eq $measurementProperty -or $null -eq $measurementProperty.Value) {
        throw [InvalidOperationException]::new('batch measurement is missing')
    }

    $measurement = $measurementProperty.Value
    $startedProperty = $measurement.PSObject.Properties['batchStartedAtUtc']
    $endedProperty = $measurement.PSObject.Properties['batchEndedAtUtc']
    $clockProperty = $measurement.PSObject.Properties['clockSnapshotAtUtc']
    if ($null -eq $startedProperty -or $null -eq $startedProperty.Value -or
        $null -eq $endedProperty -or $null -eq $endedProperty.Value -or
        $null -eq $clockProperty -or $null -eq $clockProperty.Value) {
        throw [InvalidOperationException]::new(
            'batch measurement timestamps are incomplete')
    }

    try {
        $started = [DateTimeOffset]$startedProperty.Value
        $ended = [DateTimeOffset]$endedProperty.Value
        $clock = [DateTimeOffset]$clockProperty.Value
    }
    catch {
        throw [InvalidOperationException]::new(
            'batch measurement timestamps are invalid')
    }

    if ($ended -lt $started) {
        throw [InvalidOperationException]::new(
            'batch measurement ended before it started')
    }
    if ($clock -lt $ended) {
        throw [InvalidOperationException]::new(
            'batch clock snapshot predates the read pass end')
    }

    return [ordered]@{
        batchStartedAtUtc            = $started.ToUniversalTime().ToString('o')
        batchEndedAtUtc              = $ended.ToUniversalTime().ToString('o')
        clockSnapshotAtUtc           = $clock.ToUniversalTime().ToString('o')
        readPassMilliseconds         = [Math]::Round(
            ($ended - $started).TotalMilliseconds, 3)
        clockSnapshotLagMilliseconds = [Math]::Round(
            ($clock - $ended).TotalMilliseconds, 3)
    }
}
