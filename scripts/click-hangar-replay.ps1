#Requires -Version 5.1
<#
.SYNOPSIS
  Compatibility wrapper -> scripts/play-replay-from-hangar.ps1
#>
[CmdletBinding()]
param(
    [int]$HangarTimeoutSeconds = 240,
    [int]$StepTimeoutSeconds = 30,
    [int]$ConfirmTimeoutSeconds = 45,
    [switch]$SkipConfirm
)

$target = Join-Path $PSScriptRoot 'play-replay-from-hangar.ps1'
$argsList = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', $target,
    '-HangarTimeoutSeconds', "$HangarTimeoutSeconds",
    '-StepTimeoutSeconds', "$StepTimeoutSeconds",
    '-ConfirmTimeoutSeconds', "$ConfirmTimeoutSeconds"
)
if ($SkipConfirm) { $argsList += '-SkipConfirm' }
$p = Start-Process -FilePath powershell.exe -ArgumentList $argsList -Wait -PassThru -NoNewWindow
exit [int]$p.ExitCode
