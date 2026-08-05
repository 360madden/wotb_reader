$ErrorActionPreference = 'Continue'
$p = Get-Process -Name wotblitz -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Host 'NO_GAME'; exit 1 }
Write-Host ('pid=' + $p.Id + ' title=' + $p.MainWindowTitle + ' responding=' + $p.Responding)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
Write-Host ('visible=' + [Win]::IsWindowVisible($p.MainWindowHandle) + ' fg=' + ([Win]::GetForegroundWindow() -eq $p.MainWindowHandle))
