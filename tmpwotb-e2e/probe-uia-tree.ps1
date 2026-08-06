# Dump the x32dbg UIA tree: every element with ControlType + Name + patterns,
# focused on finding the command bar / log input.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dbg = 'C:\work\tools\x64dbg\release\x32\x32dbg.exe'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$dbgProc = Start-Process -FilePath $dbg -PassThru
$win = $null
for ($i = 0; $i -lt 30; $i++) {
    $p = Get-Process -Id $dbgProc.Id -ErrorAction SilentlyContinue
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $win = $p; break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Write-Host 'NO_WINDOW'; Stop-Process -Id $dbgProc.Id -Force; exit 1 }
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Host ("total_elements=" + $all.Count)
Write-Host ''

$count = 0
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    $name = $el.Current.Name
    $cls = $el.Current.ClassName
    # Show Edit/Document/ComboBox/Custom/Text elements and anything named like a command/log
    $interesting = $ct -match 'Edit|Document|ComboBox|Custom|Text' -or $name -match 'Command|Log|Script'
    if ($interesting) {
        $patterns = @()
        foreach ($pat in $el.GetSupportedPatterns()) { $patterns += ($pat.ProgrammaticName -replace 'Pattern$', '' -replace '.*\.', '') }
        $focusable = $el.Current.IsKeyboardFocusable
        Write-Host ("[{0}] class={1} name='{2}' focusable={3} patterns={4}" -f $ct, $cls, $name, $focusable, ($patterns -join ','))
        $count++
        if ($count -ge 60) { Write-Host '...truncated'; break }
    }
}
Write-Host ("shown=" + $count)

Stop-Process -Id $dbgProc.Id -Force -ErrorAction SilentlyContinue
Write-Host 'DONE'
