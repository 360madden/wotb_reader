#Requires -Version 5.1
<#
.SYNOPSIS
  Click WoT Blitz "WATCH OFFLINE" and verify the offline gate flipped.

.DESCRIPTION
  Owner standing rule: the agent must advance the not-logged-in dialog.
  Never clicks LOG IN AND WATCH.

  Verification is NOT "hope the gate becomes verified eventually". This script:
    1. Reads GET /api/v1/game/state before clicking.
    2. Clicks candidate points on the orange WATCH OFFLINE region (left button).
    3. Polls until OfflineReplayVerified OR timeout.
    4. Writes a window screenshot to %TEMP% for visual confirmation.
    5. Exits nonzero unless the gate is OfflineReplayVerified.

.EXITCODES
  0  OfflineReplayVerified (success)
  1  Game window missing
  2  Rendezvous / capability missing
  3  Click attempted but gate did not reach OfflineReplayVerified
  4  Unexpected error
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 45,
    [int]$MaxRounds = 4,
    [string]$ScreenshotPath = $(Join-Path $env:TEMP 'wotb-watch-offline-verify.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WatchOfflineNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  // PW_RENDERFULLCONTENT
  public const uint PW_RENDERFULLCONTENT = 0x00000002;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(60);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
  }

  public static bool CaptureWindow(IntPtr hWnd, string path) {
    RECT r;
    if (!GetWindowRect(hWnd, out r)) return false;
    int w = r.Right - r.Left;
    int h = r.Bottom - r.Top;
    if (w < 32 || h < 32) return false;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      bool ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
      g.ReleaseHdc(hdc);
      if (!ok) {
        // Fallback: screen blit of window rect
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
      }
      bmp.Save(path, ImageFormat.Png);
      return true;
    }
  }
}
"@ -ReferencedAssemblies System.Drawing.dll

function Get-Rendezvous {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    return (Get-Content $file.FullName -Raw | ConvertFrom-Json)
}

function Get-VerificationState([string]$BaseUri, [hashtable]$Headers) {
    $state = Invoke-RestMethod -Uri "$BaseUri/api/v1/game/state" -Headers $Headers
    if ($state.verificationState) { return [string]$state.verificationState }
    if ($state.VerificationState) { return [string]$state.VerificationState }
    return 'Unknown'
}

function Get-GameWindow {
    $proc = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
    return $proc
}

# Relative points inside the client area aimed at the LEFT orange WATCH OFFLINE
# button (not the green LOG IN AND WATCH on the right, not CANCEL below).
$ClickPoints = @(
    @{ X = 0.38; Y = 0.54 },
    @{ X = 0.36; Y = 0.56 },
    @{ X = 0.40; Y = 0.52 },
    @{ X = 0.34; Y = 0.58 },
    @{ X = 0.42; Y = 0.55 }
)

try {
    $rv = Get-Rendezvous
    $headers = @{
        'X-WotBTreader-Capability' = "$($rv.capability)"
        'Content-Type'             = 'application/json'
    }
    $base = [string]$rv.baseUri

    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'watch_offline: no_game_window'
        exit 1
    }

    $before = Get-VerificationState $base $headers
    Write-Host "watch_offline: before=$before pid=$($game.Id)"

    if ($before -eq 'OfflineReplayVerified') {
        Write-Host 'watch_offline: already_verified'
        [void][WatchOfflineNative]::CaptureWindow($game.MainWindowHandle, $ScreenshotPath)
        Write-Host "watch_offline: screenshot=$ScreenshotPath"
        exit 0
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $verified = $false

    for ($round = 1; $round -le $MaxRounds; $round++) {
        $game = Get-GameWindow
        if (-not $game) {
            Write-Host 'watch_offline: window_lost'
            exit 1
        }

        $h = $game.MainWindowHandle
        [void][WatchOfflineNative]::ShowWindow($h, 9)
        [void][WatchOfflineNative]::SetForegroundWindow($h)
        Start-Sleep -Milliseconds 350

        $rect = New-Object WatchOfflineNative+RECT
        if (-not [WatchOfflineNative]::GetWindowRect($h, [ref]$rect)) {
            Write-Host 'watch_offline: get_rect_failed'
            exit 4
        }
        $w = $rect.Right - $rect.Left
        $ht = $rect.Bottom - $rect.Top
        Write-Host "watch_offline: round=$round size=${w}x${ht}"

        foreach ($p in $ClickPoints) {
            $x = [int]($rect.Left + $w * $p.X)
            $y = [int]($rect.Top + $ht * $p.Y)
            [WatchOfflineNative]::Click($x, $y)
            Start-Sleep -Milliseconds 220
        }

        # Capture immediately after clicks for visual proof of dialog state.
        [void][WatchOfflineNative]::CaptureWindow($h, $ScreenshotPath)
        Write-Host "watch_offline: screenshot=$ScreenshotPath"

        $pollUntil = (Get-Date).AddSeconds([Math]::Min(12, [Math]::Max(4, $TimeoutSeconds / $MaxRounds)))
        if ($pollUntil -gt $deadline) { $pollUntil = $deadline }
        do {
            Start-Sleep -Seconds 1
            $vs = Get-VerificationState $base $headers
            Write-Host "watch_offline: poll=$vs"
            if ($vs -eq 'OfflineReplayVerified') {
                $verified = $true
                break
            }
        } while ((Get-Date) -lt $pollUntil)

        if ($verified) { break }
        if ((Get-Date) -ge $deadline) { break }
    }

    $after = Get-VerificationState $base $headers
    Write-Host "watch_offline: after=$after verified=$verified"

    if ($after -ne 'OfflineReplayVerified') {
        $game = Get-GameWindow
        if ($game) {
            [void][WatchOfflineNative]::CaptureWindow($game.MainWindowHandle, $ScreenshotPath)
            Write-Host "watch_offline: final_screenshot=$ScreenshotPath"
        }
        Write-Host 'watch_offline: FAILED_gate_not_verified'
        exit 3
    }

    # Gate alone is necessary but not sufficient: lifecycle evidence can flip
    # while the not-logged-in dialog is still visible. Wait briefly and
    # re-capture so the agent can visually confirm the dialog is gone
    # (no "WATCH OFFLINE" / "LOG IN AND WATCH" / "You are not logged in").
    Start-Sleep -Seconds 3
    $game = Get-GameWindow
    if ($game) {
        [void][WatchOfflineNative]::ShowWindow($game.MainWindowHandle, 9)
        [void][WatchOfflineNative]::SetForegroundWindow($game.MainWindowHandle)
        Start-Sleep -Milliseconds 200
        [void][WatchOfflineNative]::CaptureWindow($game.MainWindowHandle, $ScreenshotPath)
        Write-Host "watch_offline: visual_verify_screenshot=$ScreenshotPath"
    }
    Write-Host 'watch_offline: SUCCESS_gate_verified_visual_check_required'
    Write-Host 'watch_offline: agent_must_confirm_screenshot_has_no_login_dialog'
    exit 0
}
catch {
    Write-Host ("watch_offline: error=" + $_.Exception.Message)
    exit 4
}
