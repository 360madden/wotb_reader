#Requires -Version 5.1
<#
.SYNOPSIS
  Replay play/pause state probe from the bottom-center replay HUD icon.

.DESCRIPTION
  WoT Blitz's replay HUD shows a play/pause symbol above the "SPACE" bar at
  the bottom center of the game window: two vertical white bars when the
  replay is paused, a white triangle when it is playing (operator-observed
  2026-08-04). This script captures the game window (PrintWindow, live DAVA
  frames) and classifies that icon into `paused` | `playing` | `unknown`.

  Purpose: give the exact-value pause scan (v3 strategy, -CompareMode exact)
  action feedback. The scan assumes the replay is paused at a known decoded
  value; this probe confirms the operator's Space pause actually froze the
  replay (icon = paused) before scanning, and detects an accidental resume
  (icon = playing) mid-run. Pixel state is advisory: the driver's
  match-then-collapse plateau remains the value-level check.

  `unknown` is returned when the icon cannot be seen (HUD hidden via the eye
  toggle, loading/menus, minimized window, or capture failure) - never
  guessed. Fail-safe direction: the driver treats anything but `paused` as
  "cannot confirm paused".

.OUTPUTS
  Writes exactly one Write-Output line, `replay_state=<paused|playing|unknown>`,
  as the machine-readable result (Write-Host lines are diagnostics only).

.EXITCODES
  0  State observed (or -WaitFor confirmed)
  1  -WaitFor timeout / state never confirmed
  2  No game window
  3  Unexpected error
  4  -SelfTest failed
#>
[CmdletBinding()]
param(
    # Wait (poll) for this state instead of a single probe.
    [ValidateSet('paused', 'playing', 'unknown')]
    [string]$WaitFor = '',
    [int]$TimeoutSeconds = 45,
    # Optional exact game process id (default: first wotblitz with a window).
    [int]$ProcessId = 0,
    # Synthesize pause/play icons and assert the classifier (no game needed).
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('ReplayStateVisionV1' -as [type])) {
Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ReplayStateVisionV1 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public const uint PW_RENDERFULLCONTENT = 0x00000002;
  static bool _dpiAware;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public static void EnsureDpiAware() {
    if (_dpiAware) return;
    try { SetProcessDPIAware(); } catch { }
    _dpiAware = true;
  }

  public static Bitmap CaptureBitmap(IntPtr hWnd, out RECT r) {
    EnsureDpiAware();
    r = new RECT();
    if (!GetWindowRect(hWnd, out r)) return null;
    int w = r.Right - r.Left;
    int h = r.Bottom - r.Top;
    if (w < 64 || h < 64) return null;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      bool ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
      g.ReleaseHdc(hdc);
      if (!ok) {
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
      }
    }
    return bmp;
  }

  // Classify the bottom-center replay HUD icon. Pause = two vertical bars
  // (empty central band); play = solid left-heavy triangle. White pixels only,
  // so the green SPACE bar and dark HUD cannot contaminate the result.
  public static string ClassifyReplayIcon(Bitmap bmp) {
    if (bmp == null) return "unknown";
    int w = bmp.Width, h = bmp.Height;
    // ROI: bottom-center, above the SPACE bar (the bar sits ~94-97% height).
    int x0 = (int)(w * 0.40), x1 = (int)(w * 0.60);
    int y0 = (int)(h * 0.80), y1 = (int)(h * 0.93);
    if (x1 <= x0 || y1 <= y0) return "unknown";

    int minX = int.MaxValue, maxX = 0, minY = int.MaxValue, maxY = 0, count = 0;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      IntPtr scan0 = data.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      for (int y = y0; y < y1; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int x = x0; x < x1; x++) {
          int i = x * 4;
          byte b = row[i], g = row[i + 1], r = row[i + 2];
          if (r > 160 && g > 160 && b > 160) {
            count++;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
          }
        }
      }
    } finally { bmp.UnlockBits(data); }

    if (count < 40) return "unknown";
    int bw = maxX - minX + 1, bh = maxY - minY + 1;
    if (bw < 12 || bh < 12) return "unknown";

    int[] density = new int[bw];
    var data2 = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data2.Stride;
      IntPtr scan0 = data2.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      for (int y = minY; y <= maxY; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int cx = 0; cx < bw; cx++) {
          int i = (minX + cx) * 4;
          byte b = row[i], g = row[i + 1], r = row[i + 2];
          if (r > 160 && g > 160 && b > 160) density[cx]++;
        }
      }
    } finally { bmp.UnlockBits(data2); }

    int maxDensity = 0;
    foreach (int d in density) if (d > maxDensity) maxDensity = d;
    if (maxDensity <= 0) return "unknown";

    // Central gap => two bars (paused). The middle 35-65% band has no dense column.
    bool centerEmpty = true;
    for (int cx = (int)(bw * 0.35); cx < (int)(bw * 0.65); cx++) {
      if (density[cx] >= 0.35 * maxDensity) { centerEmpty = false; break; }
    }
    if (centerEmpty) return "paused";

    // Left-heavy solid shape => triangle (playing).
    long leftMass = 0, rightMass = 0;
    for (int cx = 0; cx < bw; cx++) {
      if (cx < bw * 0.45) leftMass += density[cx];
      else if (cx > bw * 0.55) rightMass += density[cx];
    }
    if (rightMass > 0 && leftMass > 1.4 * rightMass) return "playing";
    return "unknown";
  }

  // Synthetic self-test: pause bars / play triangle / empty frame, placed in
  // the ROI of a 1280x720 frame. Returns "PASS" or a failure description.
  public static string SelfTest() {
    int W = 1280, H = 720;
    int cx = (int)(W * 0.50), cy = (int)(H * 0.865);

    using (var pause = new Bitmap(W, H)) {
      using (var g = Graphics.FromImage(pause)) {
        g.Clear(Color.FromArgb(30, 30, 30));
        int bw = 10;
        g.FillRectangle(Brushes.White, cx - bw - bw / 2, cy - 16, bw, 32);
        g.FillRectangle(Brushes.White, cx + bw / 2, cy - 16, bw, 32);
      }
      string r = ClassifyReplayIcon(pause);
      if (r != "paused") return "pause_icon_classified=" + r;
    }
    using (var play = new Bitmap(W, H)) {
      using (var g = Graphics.FromImage(play)) {
        g.Clear(Color.FromArgb(30, 30, 30));
        g.FillPolygon(Brushes.White, new Point[] {
          new Point(cx - 16, cy - 16),
          new Point(cx + 16, cy),
          new Point(cx - 16, cy + 16) });
      }
      string r = ClassifyReplayIcon(play);
      if (r != "playing") return "play_icon_classified=" + r;
    }
    using (var empty = new Bitmap(W, H)) {
      using (var g = Graphics.FromImage(empty)) { g.Clear(Color.FromArgb(30, 30, 30)); }
      string r = ClassifyReplayIcon(empty);
      if (r != "unknown") return "empty_classified=" + r;
    }
    return "PASS";
  }
}
"@ -ReferencedAssemblies System.Drawing.dll
}

function Get-GameWindow {
    if ($ProcessId -gt 0) {
        return Get-Process -Id $ProcessId -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
    }
    return Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Test-ReplayState {
    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'replay_state_probe: no_game_window'
        return $null
    }
    [void][ReplayStateVisionV1]::EnsureDpiAware()
    $rect = New-Object ReplayStateVisionV1+RECT
    $bmp = [ReplayStateVisionV1]::CaptureBitmap($game.MainWindowHandle, [ref]$rect)
    if (-not $bmp) {
        Write-Host 'replay_state_probe: capture_failed'
        return 'unknown'
    }
    try {
        return [ReplayStateVisionV1]::ClassifyReplayIcon($bmp)
    }
    finally {
        $bmp.Dispose()
    }
}

try {
    if ($SelfTest) {
        $result = [ReplayStateVisionV1]::SelfTest()
        if ($result -ne 'PASS') {
            Write-Host ("replay_state_probe: SELF_TEST_FAILED " + $result)
            exit 4
        }
        Write-Host 'replay_state_probe: SELF_TEST_PASS'
        Write-Output 'replay_state=unknown'
        exit 0
    }

    if ($WaitFor) {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $state = Test-ReplayState
            if ($state -eq $WaitFor) {
                Write-Host ("replay_state_probe: observed=" + $state)
                Write-Output ("replay_state=" + $state)
                exit 0
            }
            if ($null -eq $state) {
                Write-Host 'replay_state_probe: no_game_window'
                exit 2
            }
            Write-Host ("replay_state_probe: wait_for=" + $WaitFor + " current=" + $state)
            Start-Sleep -Seconds 2
        }
        Write-Host ("replay_state_probe: TIMEOUT waiting for " + $WaitFor)
        Write-Output ("replay_state=" + $state)
        exit 1
    }

    $state = Test-ReplayState
    if ($null -eq $state) {
        exit 2
    }
    Write-Host ("replay_state_probe: state=" + $state)
    Write-Output ("replay_state=" + $state)
    exit 0
}
catch {
    Write-Host ("replay_state_probe: error=" + $_.Exception.Message)
    exit 3
}
