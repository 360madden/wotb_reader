# Smoke: prove click-watch-offline.ps1's new FRESH16 click channels work on the
# real engine. Loads the same Add-Type block the script compiles (via AST
# extraction - same shape handling as probe-watch-compile.ps1), then:
#   A) ClickScreenSendInput returns true (both down/up accepted)
#   B) ClickClientMessage posts WM_LBUTTONDOWN/UP to a real scratch window
#      without crashing (message delivery itself can't be observed without a
#      subclassed wndproc, but a bad lParam pack / closed handle surfaces).
# Run under BOTH engines (pwsh 7.6 and Windows PowerShell 5.1).
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
# .Condition directly. Probe all three via PSObject (a literal
# [Tuple] type reference breaks PS 5.1 - it's a .NET ValueTuple, not a
# PowerShell language type).
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

# A) SendInput accept test: move to a screen corner (2,2) so nothing real is
#    clicked, prove both down+up are accepted by the input system.
$ok = [WatchOfflineVisionV3]::ClickScreenSendInput(2, 2, 20, 30)
if (-not $ok) { throw 'FAIL: ClickScreenSendInput returned false' }
Write-Host 'SENDINPUT_CHANNEL_OK'

# B) PostMessage channel against a scratch top-level window.
Add-Type -AssemblyName System.Windows.Forms
$scratch = New-Object System.Windows.Forms.Form -Property @{ Text = 'wt-click-channel-probe'; Width = 80; Height = 80 }
$h = $scratch.Handle
$scratch.Show()
try {
    [WatchOfflineVisionV3]::ClickClientMessage($h, 20, 20)
    Write-Host 'MESSAGE_CHANNEL_OK'
}
finally {
    $scratch.Dispose()
}
Write-Host ('CHANNELS_OK engine=' + $PSVersionTable.PSEdition + ' ps=' + $PSVersionTable.PSVersion.ToString())
