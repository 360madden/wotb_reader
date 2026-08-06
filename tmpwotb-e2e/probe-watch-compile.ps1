# Smoke: prove click-watch-offline.ps1's WatchOfflineVisionV3 C# Add-Type block
# compiles on the RUNNING engine (the 2026-08-06 FRESH15 blocker: pwsh 7 could
# not resolve 'System.Drawing.dll' -> CS1069). Extracts the exact
# `if (-not ('WatchOfflineVisionV3' -as [type])) { ... }` block via AST and
# executes it, then asserts the type loaded. Run under BOTH engines:
#   powershell -File tmpwotb-e2e/probe-watch-compile.ps1
#   pwsh        -File tmpwotb-e2e/probe-watch-compile.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $repo 'scripts\click-watch-offline.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'source parse failed: ' + $errors[0].Message }

# AST shape differs across PowerShell versions: pwsh 7.6 reworked
# IfStatementAst.Clauses into Tuple<condition,block> (Item1 = condition);
# intermediate versions expose Clauses[].Condition; older engines expose
# .Condition directly. Probe all three via PSObject so the guard block is
# found on any engine.
$ifStmt = $ast.FindAll({
    param($n)
    if ($n -isnot [System.Management.Automation.Language.IfStatementAst]) { return $false }
    $condText = $null
    if ($n.PSObject.Properties['Clauses'] -and $n.Clauses.Count -gt 0) {
        $first = $n.Clauses[0]
        if ($first.PSObject.Properties['Item1']) { $condText = $first.Item1.Extent.Text }
        elseif ($first.PSObject.Properties['Condition']) { $condText = $first.Condition.Extent.Text }
    }
    elseif ($n.PSObject.Properties['Condition']) {
        $condText = $n.Condition.Extent.Text
    }
    return ($null -ne $condText -and $condText.Contains('WatchOfflineVisionV3'))
}, $true) | Select-Object -First 1
if (-not $ifStmt) { throw 'WatchOfflineVisionV3 Add-Type guard block not found' }

. ([scriptblock]::Create($ifStmt.Extent.Text))
if (-not ('WatchOfflineVisionV3' -as [type])) { throw 'FAIL: WatchOfflineVisionV3 did not load' }

# Exercise the core path so a link error (not just a compile error) surfaces:
$hwnd = [IntPtr]::Zero
$rect = New-Object WatchOfflineVisionV3+RECT
$bmp = [WatchOfflineVisionV3]::CaptureBitmap($hwnd, [ref]$rect)
if ($null -ne $bmp) { $bmp.Dispose() }
Write-Host ('WATCH_COMPILE_OK engine=' + $PSVersionTable.PSEdition + ' ps=' + $PSVersionTable.PSVersion.ToString() + ' drawing=' + ([System.Drawing.Bitmap].Assembly.GetName().Name))
