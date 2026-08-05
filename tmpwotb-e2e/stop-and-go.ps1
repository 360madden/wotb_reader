$ErrorActionPreference = 'Continue'
# Stop stale processes (the launch script also self-stops a stale host, but be explicit)
Get-Process -Name wotblitz, WotBTreader.Host.Web, x32dbg -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$left = @(Get-Process -Name wotblitz, WotBTreader.Host.Web -ErrorAction SilentlyContinue)
Write-Host ("cleanup: left=" + $left.Count)
& 'C:\work\wotb_reader\tmpwotb-e2e\od-049-autoloop.ps1'
Write-Host ("autoloop_exit=" + $LASTEXITCODE)
exit $LASTEXITCODE
