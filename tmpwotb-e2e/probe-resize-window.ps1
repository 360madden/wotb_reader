# Smoke: prove launch-offline-replay-for-od.ps1's FRESH17 window resize works
# (SetWindowPos via OdLaunch.WindowResize, DPI-aware, restore-if-zoomed path)
# and that the clicker's PxScale threshold math scales 1080p-tuned thresholds
# down to a 640x360 window. Run under BOTH engines:
#   powershell -File tmpwotb-e2e/probe-resize-window.ps1
#   pwsh        -File tmpwotb-e2e/probe-resize-window.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# 1. Load the launch script's OdLaunch.WindowResize type (AST-extract the
#    guarded Add-Type block - same shape handling as probe-watch-compile.ps1).
$src = Join-Path $repo 'scripts\launch-offline-replay-for-od.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'launch source parse failed' }
$addTypeStmt = $ast.FindAll({
    param($n)
    $n -is [System.Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -eq 'Add-Type' -and
    $n.ToString().Contains('class WindowResize')
}, $true) | Select-Object -First 1
if (-not $addTypeStmt) { throw 'OdLaunch.WindowResize Add-Type block not found' }
. ([scriptblock]::Create($addTypeStmt.Extent.Text))
if (-not ('OdLaunch.WindowResize' -as [type])) { throw 'FAIL: OdLaunch.WindowResize did not load' }

# 2. Resize a real scratch window and verify the rect changed.
Add-Type -AssemblyName System.Windows.Forms
$scratch = New-Object System.Windows.Forms.Form -Property @{ Text = 'wt-resize-probe'; Width = 300; Height = 200; StartPosition = 'Manual'; Left = 40; Top = 40 }
$scratch.Show()
try {
    $before = New-Object OdLaunch.WindowResize+RECT
    [void][OdLaunch.WindowResize]::GetWindowRect($scratch.Handle, [ref]$before)
    $ok = [OdLaunch.WindowResize]::Resize($scratch.Handle, 640, 360, 0, 0)
    Start-Sleep -Milliseconds 300
    $after = New-Object OdLaunch.WindowResize+RECT
    [void][OdLaunch.WindowResize]::GetWindowRect($scratch.Handle, [ref]$after)
    $w = $after.Right - $after.Left
    $h = $after.Bottom - $after.Top
    Write-Host ("RESIZE_OK ret=" + $ok + " before=" + ($before.Right - $before.Left) + "x" + ($before.Bottom - $before.Top) + " after=" + $w + "x" + $h)
    if (-not $ok -or $w -lt 620 -or $h -lt 340) { throw 'FAIL: window did not resize to ~640x360' }
}
finally {
    $scratch.Dispose()
}

# 3. PxScale math: a 640x360 window vs the 1920x1080 reference (0.111), and the
#    scaled thresholds the clicker derives. Mirror the clicker's formula.
$refW = 1920.0; $refH = 1080.0
$pxScale640 = [Math]::Max(0.05, (640.0 * 360.0) / ($refW * $refH))
$readyMin = [Math]::Max(5, [int](2000 * $pxScale640))
$minBlob = [Math]::Max(5, [int](400 * $pxScale640))
$dismiss = [Math]::Max(5, [int](120 * $pxScale640))
$syncCeil = [Math]::Max(5, [int](400 * $pxScale640))
Write-Host ("PXSCALE_640x360=" + [Math]::Round($pxScale640, 4) + " readyMin=" + $readyMin + " minBlob=" + $minBlob + " dismiss=" + $dismiss + " syncCeil=" + $syncCeil)
# Sanity: at the small size the scaled ready threshold must still be >= 200 so
# the gate can fire on a real button, and never 0.
if ($readyMin -lt 200) { throw 'FAIL: scaled ready threshold too low to ever fire' }
if ($minBlob -lt 5 -or $dismiss -lt 5 -or $syncCeil -lt 5) { throw 'FAIL: scaled threshold floor broken' }
Write-Host ('PROBE_RESIZE_OK engine=' + $PSVersionTable.PSEdition + ' ps=' + $PSVersionTable.PSVersion.ToString())
