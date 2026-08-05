# Probe: how long does x32dbg.exe -p <pid> take to create its main window?
# Launches a short-lived dummy target, attaches x32dbg, polls MainWindowHandle.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dbgRoots = @('C:\work\tools\x64dbg', 'C:\x64dbg', 'C:\tools\x64dbg')
$dbg = $null
foreach ($r in $dbgRoots) {
    $c = Join-Path $r 'release\x32\x32dbg.exe'
    if (Test-Path -LiteralPath $c) { $dbg = $c; break }
    $x = Join-Path $r 'release\x64\x64dbg.exe'
    if (Test-Path -LiteralPath $x) { $dbg = $x; break }
}
if (-not $dbg) { Write-Host 'NO_DEBUGGER_FOUND'; exit 1 }
Write-Host ("dbg=" + $dbg)

# Dummy target that lives ~60s
$target = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -PassThru
Write-Host ("target_pid=" + $target.Id)
Start-Sleep -Milliseconds 500

$t0 = [DateTime]::UtcNow
$dbgProc = Start-Process -FilePath $dbg -ArgumentList @('-p', "$($target.Id)") -PassThru
Write-Host ("debugger_pid=" + $dbgProc.Id)

$elapsedAtWindow = $null
$windowTitle = $null
$deadline = (Get-Date).AddSeconds(25)
$poll = 0
while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if (-not $p) {
        Write-Host ('POLL ' + $poll + ': process EXITED (elapsed ' + [math]::Round(((Get-Date).ToUniversalTime() - $t0).TotalSeconds, 1) + 's)')
        break
    }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
        $elapsedAtWindow = [math]::Round(((Get-Date).ToUniversalTime() - $t0).TotalSeconds, 1)
        $windowTitle = $p.MainWindowTitle
        Write-Host ('WINDOW at ' + $elapsedAtWindow + 's title="' + $windowTitle + '"')
        break
    }
    $poll++
    Write-Host ('POLL ' + $poll + ': handle=0 (elapsed ' + [math]::Round(((Get-Date).ToUniversalTime() - $t0).TotalSeconds, 1) + 's)')
    Start-Sleep -Milliseconds 500
}

if ($null -eq $elapsedAtWindow) {
    Write-Host 'NO_WINDOW_IN_25S'
    # Is the process even alive? Does it have ANY top-level windows?
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host ('alive=True respond=' + $p.Responding + ' mainWindowTitle="' + $p.MainWindowTitle + '"')
    }
    else { Write-Host 'alive=False' }
}

# Cleanup
Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
