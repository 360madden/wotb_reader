param(
    [string]$Repo = 'C:\work\wotb_reader',
    [string]$Run = 'fresh15i'
)
$ErrorActionPreference = 'Continue'
$log = Join-Path $Repo (".data\od-049-$Run.log")
$err = Join-Path $Repo (".data\od-049-$Run.err.log")
$res = Join-Path $Repo (".data\od-049-$Run-result.json")
Remove-Item $log, $err, $res -Force -ErrorAction SilentlyContinue
$argsList = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $Repo 'tmpwotb-e2e\od-049-autoloop.ps1'),
    '-ReplayPath', (Join-Path $Repo '.data\launch\a9aed0467d7843efb06bb3319bb52ded.wotbreplay'),
    '-MaxReadRounds', '70',
    '-StageTopN', '2',
    '-StageDelaySeconds', '2',
    '-AttachSmokeOnFirstRound',
    '-StageViewpointOnly',
    '-ResultPath', $res
)
$p = Start-Process -FilePath 'pwsh' -ArgumentList $argsList `
    -RedirectStandardOutput $log -RedirectStandardError $err -PassThru -WindowStyle Hidden
$p.Id | Set-Content (Join-Path $Repo ".data\$Run-pid.txt")
Write-Output ("LAUNCH_PID=" + $p.Id)
