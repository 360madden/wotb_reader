#Requires -Version 5.1
<#
.SYNOPSIS
  Start/attach game → hangar Battle ready → profile → REPLAYS → play.

.DESCRIPTION
  Spec: docs/superpowers/specs/2026-08-02-hangar-replays-play.md

  Does not launch a .wotbreplay via argv (avoids LoginOnReplayDialog / Error 126).
  Reuses an existing wotblitz window when present; otherwise starts wotblitz.exe
  with no replay argument (launcher may already be running).

  Templates (optional): scripts/ui-templates/hangar/{profile-hex,replays-label,play-triangle}.png
  Color/ROI heuristics used when templates miss. Never logs private paths, tokens,
  or account ids. Diagnostic screenshot: %TEMP%\wotb-hangar-replay-verify.png

.EXITCODES
  0  Playback confirmed (START_REPLAY_LOCAL / Start replay event)
  1  No game window / start failed
  2  Hangar Battle never appeared
  3  Profile / REPLAYS / play sequence failed
  4  Unexpected
  5  Error dialog / replay failed after play
#>
[CmdletBinding()]
param(
    [int]$HangarTimeoutSeconds = 240,
    [int]$StepTimeoutSeconds = 30,
    [int]$ConfirmTimeoutSeconds = 45,
    [int]$SampleIntervalMs = 250,
    [int]$MinBattleOrange = 1500,
    [int]$StableBattleSamples = 2,
    [double]$DimMaxLuminance = 42,
    [string]$ScreenshotPath,
    [string]$TemplateDir,
    [string]$RepoRoot,
    [switch]$SkipConfirm,
    [switch]$SkipWatchOfflineChain
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $ScreenshotPath = Join-Path $env:TEMP 'wotb-hangar-replay-verify.png'
}
if ([string]::IsNullOrWhiteSpace($TemplateDir)) {
    $TemplateDir = Join-Path $scriptDir 'ui-templates\hangar'
}
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptDir
}

Add-Type -AssemblyName System.Drawing -ErrorAction Stop

Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class HangarPlayVision {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  public const uint WM_LBUTTONDOWN = 0x0201;
  public const uint WM_LBUTTONUP = 0x0202;

  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int X, Y; }

  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint PW_RENDERFULLCONTENT = 0x00000002;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public struct BlobHit {
    public bool Found;
    public int PixelCount;
    public int CentroidX;
    public int CentroidY;
    public double Score; // lower SAD-per-pixel is better for templates; unused for color
  }

  public static void SoftForeground(IntPtr h) { SetForegroundWindow(h); }

  public static void ForceForeground(IntPtr h) {
    ShowWindow(h, 9);
    keybd_event(0x12, 0, 0, UIntPtr.Zero);
    SetForegroundWindow(h);
    keybd_event(0x12, 0, 2, UIntPtr.Zero);
  }

  public static void ClickScreen(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(40);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
  }

  public static void ClickClient(IntPtr hWnd, int clientX, int clientY) {
    // Bitmap coords from PrintWindow match client area for borderless/game windows.
    IntPtr lp = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
    SendMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)1, lp);
    System.Threading.Thread.Sleep(40);
    SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lp);
  }

  public static Bitmap CaptureBitmap(IntPtr hWnd, out RECT r) {
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

  public static bool SaveBitmap(Bitmap bmp, string path) {
    if (bmp == null || string.IsNullOrEmpty(path)) return false;
    try {
      string dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
      string tmp = path + ".tmp.png";
      bmp.Save(tmp, ImageFormat.Png);
      if (File.Exists(path)) File.Delete(path);
      File.Move(tmp, path);
      return true;
    } catch {
      try {
        string alt = path + "." + DateTime.UtcNow.Ticks + ".png";
        bmp.Save(alt, ImageFormat.Png);
        return true;
      } catch {
        return false;
      }
    }
  }

  static bool IsBattleOrange(byte r, byte g, byte b) {
    if (r < 170) return false;
    if (g < 70 || g > 210) return false;
    if (b > 110) return false;
    if (r < g + 25) return false;
    if (r < b + 80) return false;
    return true;
  }

  static bool IsProfileBlue(byte r, byte g, byte b) {
    if (b < 140) return false;
    if (b < r + 30) return false;
    if (b < g + 20) return false;
    if (r > 140) return false;
    return true;
  }

  static bool IsBrightWhite(byte r, byte g, byte b) {
    return r >= 210 && g >= 210 && b >= 210;
  }

  static bool IsAffirmativeGreen(byte r, byte g, byte b) {
    if (g < 140) return false;
    if (r > 120 || b > 120) return false;
    if (g < r + 40 || g < b + 40) return false;
    return true;
  }

  public static double MeanLuminance(Bitmap bmp, double x0f, double x1f, double y0f, double y1f) {
    if (bmp == null) return 255;
    int w = bmp.Width, h = bmp.Height;
    int x0 = Math.Max(0, (int)(w * x0f));
    int x1 = Math.Min(w, (int)(w * x1f));
    int y0 = Math.Max(0, (int)(h * y0f));
    int y1 = Math.Min(h, (int)(h * y1f));
    if (x1 <= x0 || y1 <= y0) return 255;
    double sum = 0; int n = 0;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      IntPtr scan0 = data.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      for (int y = y0; y < y1; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int x = x0; x < x1; x++) {
          int i = x * 4;
          sum += 0.2126 * row[i + 2] + 0.7152 * row[i + 1] + 0.0722 * row[i];
          n++;
        }
      }
    } finally { bmp.UnlockBits(data); }
    return n > 0 ? sum / n : 255;
  }

  static BlobHit FindColor(
      Bitmap bmp, double x0f, double x1f, double y0f, double y1f,
      Func<byte, byte, byte, bool> pred)
  {
    var hit = new BlobHit();
    if (bmp == null) return hit;
    int w = bmp.Width, h = bmp.Height;
    int x0 = Math.Max(0, (int)(w * x0f));
    int x1 = Math.Min(w, (int)(w * x1f));
    int y0 = Math.Max(0, (int)(h * y0f));
    int y1 = Math.Min(h, (int)(h * y1f));
    if (x1 <= x0 || y1 <= y0) return hit;
    long sumX = 0, sumY = 0; int count = 0;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      IntPtr scan0 = data.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      for (int y = y0; y < y1; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int x = x0; x < x1; x++) {
          int i = x * 4;
          if (!pred(row[i + 2], row[i + 1], row[i])) continue;
          count++; sumX += x; sumY += y;
        }
      }
    } finally { bmp.UnlockBits(data); }
    hit.PixelCount = count;
    if (count > 0) {
      hit.Found = true;
      hit.CentroidX = (int)(sumX / count);
      hit.CentroidY = (int)(sumY / count);
    }
    return hit;
  }

  // Coarse template match (SAD on step grid). Returns best centroid in client coords.
  public static BlobHit MatchTemplate(
      Bitmap hay, Bitmap needle,
      double x0f, double x1f, double y0f, double y1f,
      double maxMeanSad)
  {
    var hit = new BlobHit();
    if (hay == null || needle == null) return hit;
    int w = hay.Width, h = hay.Height;
    int nw = needle.Width, nh = needle.Height;
    if (nw < 8 || nh < 8 || nw >= w || nh >= h) return hit;
    int x0 = Math.Max(0, (int)(w * x0f));
    int x1 = Math.Min(w - nw, (int)(w * x1f) - nw);
    int y0 = Math.Max(0, (int)(h * y0f));
    int y1 = Math.Min(h - nh, (int)(h * y1f) - nh);
    if (x1 < x0 || y1 < y0) return hit;

    int step = Math.Max(2, Math.Min(nw, nh) / 10);
    int sample = Math.Max(2, Math.Min(nw, nh) / 16);
    double best = double.MaxValue;
    int bestX = 0, bestY = 0;

    // Managed SAD (portable; no /unsafe).
    var hayData = hay.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var ndData = needle.LockBits(new Rectangle(0, 0, nw, nh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      byte[] hayRow = new byte[Math.Abs(hayData.Stride)];
      byte[] ndRow = new byte[Math.Abs(ndData.Stride)];
      // Preload needle samples.
      int maxSamples = ((nw + sample - 1) / sample) * ((nh + sample - 1) / sample);
      int[] nx = new int[maxSamples];
      int[] ny = new int[maxSamples];
      byte[] nr = new byte[maxSamples];
      byte[] ng = new byte[maxSamples];
      byte[] nb = new byte[maxSamples];
      int sn = 0;
      for (int y = 0; y < nh; y += sample) {
        Marshal.Copy(IntPtr.Add(ndData.Scan0, y * ndData.Stride), ndRow, 0, ndRow.Length);
        for (int x = 0; x < nw; x += sample) {
          int i = x * 4;
          // Skip near-transparent / black chrome in templates.
          if (ndRow[i + 3] < 40) continue;
          nx[sn] = x; ny[sn] = y;
          nb[sn] = ndRow[i]; ng[sn] = ndRow[i + 1]; nr[sn] = ndRow[i + 2];
          sn++;
        }
      }
      if (sn < 12) return hit;

      for (int y = y0; y <= y1; y += step) {
        for (int x = x0; x <= x1; x += step) {
          long sad = 0;
          for (int s = 0; s < sn; s++) {
            int hy = y + ny[s];
            Marshal.Copy(IntPtr.Add(hayData.Scan0, hy * hayData.Stride), hayRow, 0, hayRow.Length);
            int hx = (x + nx[s]) * 4;
            sad += Math.Abs(hayRow[hx + 2] - nr[s]);
            sad += Math.Abs(hayRow[hx + 1] - ng[s]);
            sad += Math.Abs(hayRow[hx] - nb[s]);
          }
          double mean = sad / (double)(sn * 3);
          if (mean < best) { best = mean; bestX = x + nw / 2; bestY = y + nh / 2; }
        }
      }
    } finally {
      hay.UnlockBits(hayData);
      needle.UnlockBits(ndData);
    }

    if (best <= maxMeanSad) {
      hit.Found = true;
      hit.CentroidX = bestX;
      hit.CentroidY = bestY;
      hit.Score = best;
      hit.PixelCount = 1;
    }
    return hit;
  }

  public static BlobHit FindBattle(Bitmap bmp) {
    return FindColor(bmp, 0.35, 0.65, 0.08, 0.28, IsBattleOrange);
  }

  public static BlobHit FindProfileColor(Bitmap bmp) {
    return FindColor(bmp, 0.00, 0.12, 0.00, 0.14, IsProfileBlue);
  }

  public static BlobHit FindReplaysColor(Bitmap bmp) {
    return FindColor(bmp, 0.72, 0.98, 0.05, 0.22, IsBrightWhite);
  }

  // Center-top "REPLAYS" title when already inside the list screen.
  public static BlobHit FindReplaysScreenTitle(Bitmap bmp) {
    return FindColor(bmp, 0.30, 0.70, 0.02, 0.16, IsBrightWhite);
  }

  public static BlobHit FindPlayColor(Bitmap bmp) {
    return FindPlayTriangle(bmp, 0.08, 0.42, 0.28, 0.55);
  }

  public static BlobHit FindPlayColorWide(Bitmap bmp) {
    return FindPlayTriangle(bmp, 0.05, 0.95, 0.20, 0.62);
  }

  // White play-triangle centroid only (never the dark circle or sparks).
  public static BlobHit FindPlayTriangle(Bitmap bmp, double x0f, double x1f, double y0f, double y1f) {
    var hit = new BlobHit();
    if (bmp == null) return hit;
    int w = bmp.Width, h = bmp.Height;
    int x0 = Math.Max(0, (int)(w * x0f));
    int x1 = Math.Min(w, (int)(w * x1f));
    int y0 = Math.Max(0, (int)(h * y0f));
    int y1 = Math.Min(h, (int)(h * y1f));
    if (x1 - x0 < 24 || y1 - y0 < 24) return hit;

    int cell = Math.Max(8, Math.Min(w, h) / 55);
    int cols = Math.Max(1, (x1 - x0) / cell);
    int rows = Math.Max(1, (y1 - y0) / cell);
    int[,] dens = new int[rows, cols];
    long[,] sx = new long[rows, cols];
    long[,] sy = new long[rows, cols];
    int[,] minX = new int[rows, cols];
    int[,] maxX = new int[rows, cols];
    int[,] minY = new int[rows, cols];
    int[,] maxY = new int[rows, cols];
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < cols; c++) {
        minX[r, c] = int.MaxValue; minY[r, c] = int.MaxValue;
      }

    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      byte[] row = new byte[Math.Abs(data.Stride)];
      for (int y = y0; y < y1; y++) {
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
        for (int x = x0; x < x1; x++) {
          int i = x * 4;
          if (!IsBrightWhite(row[i + 2], row[i + 1], row[i])) continue;
          int cy = (y - y0) / cell, cx = (x - x0) / cell;
          if (cy < 0 || cy >= rows || cx < 0 || cx >= cols) continue;
          dens[cy, cx]++; sx[cy, cx] += x; sy[cy, cx] += y;
          if (x < minX[cy, cx]) minX[cy, cx] = x;
          if (y < minY[cy, cx]) minY[cy, cx] = y;
          if (x > maxX[cy, cx]) maxX[cy, cx] = x;
          if (y > maxY[cy, cx]) maxY[cy, cx] = y;
        }
      }
    } finally { bmp.UnlockBits(data); }

    int best = 0, br = -1, bc = -1;
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < cols; c++) {
        int d = dens[r, c];
        if (d < 40 || d > 1200) continue;
        int bw = maxX[r, c] - minX[r, c] + 1;
        int bh = maxY[r, c] - minY[r, c] + 1;
        if (bw < 8 || bh < 8) continue;
        if (bw > cell * 4 || bh > cell * 4) continue;
        double aspect = bw / (double)Math.Max(1, bh);
        if (aspect < 0.5 || aspect > 2.0) continue;
        // Densest compact white glyph; break ties toward top-left (first card).
        if (d > best || (d == best && (r < br || (r == br && c < bc)))) {
          best = d; br = r; bc = c;
        }
      }
    if (br < 0) return hit;

    int seedX = (int)(sx[br, bc] / best);
    int seedY = (int)(sy[br, bc] / best);
    return RefineWhiteCentroid(bmp, seedX, seedY, Math.Max(14, cell + 4), best);
  }

  public static BlobHit RefinePlayTriangleClick(Bitmap bmp, BlobHit seed) {
    if (bmp == null || !seed.Found) return seed;
    int pad = Math.Max(16, Math.Min(bmp.Width, bmp.Height) / 40);
    return RefineWhiteCentroid(bmp, seed.CentroidX, seed.CentroidY, pad, seed.PixelCount);
  }

  static BlobHit RefineWhiteCentroid(Bitmap bmp, int seedX, int seedY, int pad, int fallbackPx) {
    var hit = new BlobHit();
    int w = bmp.Width, h = bmp.Height;
    int rx0 = Math.Max(0, seedX - pad);
    int rx1 = Math.Min(w, seedX + pad);
    int ry0 = Math.Max(0, seedY - pad);
    int ry1 = Math.Min(h, seedY + pad);
    long sumX = 0, sumY = 0;
    int count = 0;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      byte[] row = new byte[Math.Abs(data.Stride)];
      for (int y = ry0; y < ry1; y++) {
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
        for (int x = rx0; x < rx1; x++) {
          int i = x * 4;
          if (!IsBrightWhite(row[i + 2], row[i + 1], row[i])) continue;
          count++; sumX += x; sumY += y;
        }
      }
    } finally { bmp.UnlockBits(data); }

    hit.Found = true;
    if (count >= 12) {
      hit.PixelCount = count;
      // Pure white-pixel centroid = inside the triangle.
      hit.CentroidX = (int)(sumX / count);
      hit.CentroidY = (int)(sumY / count);
    } else {
      hit.PixelCount = fallbackPx;
      hit.CentroidX = seedX;
      hit.CentroidY = seedY;
    }
    return hit;
  }

  public static BlobHit FindAffirmative(Bitmap bmp) {
    return FindColor(bmp, 0.28, 0.72, 0.48, 0.78, IsAffirmativeGreen);
  }

  public static BlobHit FindBrightRoi(Bitmap bmp, double x0f, double x1f, double y0f, double y1f, int minPx) {
    BlobHit h = FindColor(bmp, x0f, x1f, y0f, y1f, IsBrightWhite);
    if (h.Found && h.PixelCount >= minPx) return h;
    return new BlobHit();
  }

  static bool IsWatchOfflineOrange(byte r, byte g, byte b) {
    if (r < 170) return false;
    if (g < 70 || g > 210) return false;
    if (b > 110) return false;
    if (r < g + 25) return false;
    if (r < b + 80) return false;
    return true;
  }

  public static BlobHit FindColorWatchOffline(Bitmap bmp) {
    // Left/center dialog band — same ROI as click-watch-offline.ps1.
    return FindColor(bmp, 0.18, 0.55, 0.40, 0.70, IsWatchOfflineOrange);
  }
}
"@ -ReferencedAssemblies System.Drawing.dll

function Write-Hangar([string]$Message) {
    Write-Host ("hangar_play: " + $Message)
}

function Get-GameWindow {
    return Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Wait-GameWindowSoft([int]$Seconds) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        $g = Get-GameWindow
        if ($g) { return $g }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Resolve-GameExe {
    $candidates = @(
        'C:\Games\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\World_of_Tanks_Blitz\wotblitz.exe',
        'C:\Program Files (x86)\Steam\steamapps\common\World of Tanks Blitz\wotblitz.exe',
        (Join-Path $env:LOCALAPPDATA 'World_of_Tanks_Blitz\wotblitz.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function Ensure-Game {
    $existing = Get-GameWindow
    if ($existing) {
        Write-Hangar ("reuse_window pid=" + $existing.Id)
        try { [HangarPlayVision]::SoftForeground($existing.MainWindowHandle) } catch { }
        return $existing
    }

    # Process without HWND yet (still loading).
    $proc = Get-Process -Name wotblitz -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        Write-Hangar ("wait_existing_process pid=" + $proc.Id)
    }
    else {
        $exe = Resolve-GameExe
        if (-not $exe) {
            Write-Hangar 'FAILED_exe_not_found'
            return $null
        }
        Write-Hangar 'start_wotblitz_no_replay_argv'
        Start-Process -FilePath $exe -WindowStyle Normal | Out-Null
    }

    $deadline = (Get-Date).AddSeconds($HangarTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $g = Get-GameWindow
        if ($g) {
            Write-Hangar ("window_ready pid=" + $g.Id)
            return $g
        }
        Start-Sleep -Seconds 1
    }
    Write-Hangar 'FAILED_no_window'
    return $null
}

function Get-Template([string]$Name) {
    $path = Join-Path $TemplateDir $Name
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Hangar ("template_missing=" + $Name)
        return $null
    }
    try {
        return New-Object System.Drawing.Bitmap $path
    }
    catch {
        Write-Hangar ("template_load_failed=" + $Name)
        return $null
    }
}

function Invoke-Capture([IntPtr]$Hwnd) {
    $rect = New-Object HangarPlayVision+RECT
    $bmp = [HangarPlayVision]::CaptureBitmap($Hwnd, [ref]$rect)
    if (-not $bmp) { return $null }
    return [pscustomobject]@{ Bitmap = $bmp; Rect = $rect }
}

function Invoke-ClickBlob($Capture, $Blob, [string]$Label) {
    $sx = $Capture.Rect.Left + $Blob.CentroidX
    $sy = $Capture.Rect.Top + $Blob.CentroidY
    $score = if ($null -ne $Blob.Score) { [math]::Round([double]$Blob.Score, 1) } else { -1 }
    Write-Hangar ("click_$Label at=$($Blob.CentroidX),$($Blob.CentroidY) px=$($Blob.PixelCount) score=$score")
    [HangarPlayVision]::ClickScreen($sx, $sy)
}

function Test-DimOverlay([System.Drawing.Bitmap]$Bmp) {
    # True modal only: Error AFFIRMATIVE or WATCH OFFLINE orange. Dark REPLAYS list is normal.
    $aff = [HangarPlayVision]::FindAffirmative($Bmp)
    if ($aff.Found -and $aff.PixelCount -ge 500) { return $true }
    $watch = [HangarPlayVision]::FindColorWatchOffline($Bmp)
    if ($watch.Found -and $watch.PixelCount -ge 1500) { return $true }
    return $false
}

function Wait-AndClick(
    [scriptblock]$Finder,
    [int]$TimeoutSeconds,
    [string]$Label,
    [switch]$AllowDimClick,
    [switch]$ForceFocus
) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastFocus = [datetime]::MinValue
    while ((Get-Date) -lt $deadline) {
        $game = Get-GameWindow
        if (-not $game) { Write-Hangar 'no_game_window'; return $false }

        if (((Get-Date) - $lastFocus).TotalSeconds -ge 2.5) {
            try {
                if ($ForceFocus) { [HangarPlayVision]::ForceForeground($game.MainWindowHandle) }
                else { [HangarPlayVision]::SoftForeground($game.MainWindowHandle) }
            } catch { }
            $lastFocus = Get-Date
        }

        $cap = Invoke-Capture $game.MainWindowHandle
        if (-not $cap) { Start-Sleep -Milliseconds $SampleIntervalMs; continue }

        try {
            # Avoid thrashing the same PNG every sample (GDI+ lock → exit 4).
            if ($ScreenshotPath -and ((Get-Random -Maximum 5) -eq 0)) {
                [void][HangarPlayVision]::SaveBitmap($cap.Bitmap, $ScreenshotPath)
            }

            $aff = [HangarPlayVision]::FindAffirmative($cap.Bitmap)
            if ($aff.Found -and $aff.PixelCount -ge 1800) {
                Write-Hangar 'dismiss_error_affirmative'
                Invoke-ClickBlob $cap $aff 'affirmative'
                Start-Sleep -Milliseconds 900
                continue
            }

            $blob = & $Finder $cap.Bitmap
            if ($blob -and $blob.Found) {
                if ($ScreenshotPath) { [void][HangarPlayVision]::SaveBitmap($cap.Bitmap, $ScreenshotPath) }
                Invoke-ClickBlob $cap $blob $Label
                return $true
            }
        }
        finally {
            if ($cap.Bitmap) { $cap.Bitmap.Dispose() }
        }
        Start-Sleep -Milliseconds $SampleIntervalMs
    }
    Write-Hangar ("timeout_$Label")
    return $false
}

function Wait-HangarReady {
    $deadline = (Get-Date).AddSeconds($HangarTimeoutSeconds)
    $stable = 0
    $lastFocus = [datetime]::MinValue
    Write-Hangar ("looking_for_hangar timeout=${HangarTimeoutSeconds}s")
    while ((Get-Date) -lt $deadline) {
        $game = Get-GameWindow
        if (-not $game) { Write-Hangar 'no_game_window'; return $false }

        # Soft focus only once at hangar entry — mid-wait focus churn has closed
        # the game window (OnBackground → WindowDestroyed).
        if ($lastFocus -eq [datetime]::MinValue) {
            try { [HangarPlayVision]::SoftForeground($game.MainWindowHandle) } catch { }
            $lastFocus = Get-Date
        }

        $cap = Invoke-Capture $game.MainWindowHandle
        if (-not $cap) { Start-Sleep -Milliseconds $SampleIntervalMs; continue }
        try {
            if ($ScreenshotPath -and ((Get-Random -Maximum 4) -eq 0)) {
                [void][HangarPlayVision]::SaveBitmap($cap.Bitmap, $ScreenshotPath)
            }

            # Error 126 AFFIRMATIVE: centered green CTA. Never dismiss while Battle is visible
            # (hangar also has green accents). Mid-grey modal is not always "dim" by luminance.
            $battleProbe = [HangarPlayVision]::FindBattle($cap.Bitmap)
            $aff = [HangarPlayVision]::FindAffirmative($cap.Bitmap)
            $hangarVisible = $battleProbe.Found -and $battleProbe.PixelCount -ge 800
            if (-not $hangarVisible -and $aff.Found -and $aff.PixelCount -ge 1800) {
                Write-Hangar ("dismiss_error_affirmative px=$($aff.PixelCount)")
                try { [HangarPlayVision]::SoftForeground($game.MainWindowHandle) } catch { }
                Invoke-ClickBlob $cap $aff 'affirmative'
                $stable = 0
                Start-Sleep -Milliseconds 1500
                if (-not (Get-GameWindow)) {
                    Write-Hangar 'wait_window_after_affirmative'
                    $null = Wait-GameWindowSoft -Seconds 45
                }
                continue
            }

            # Do not treat hangar orange sparks / dark garage as a modal — only Error AFFIRMATIVE above.

            # Already on REPLAYS only if title + left-card white play triangle
            # (title/header whites alone false-positive and steal the play click).
            $replaysTitle = [HangarPlayVision]::FindReplaysScreenTitle($cap.Bitmap)
            $battle = $battleProbe
            if ((-not ($battle.Found -and $battle.PixelCount -ge $MinBattleOrange)) -and `
                $replaysTitle.Found -and $replaysTitle.PixelCount -ge 80) {
                $playProbe = [HangarPlayVision]::FindPlayColor($cap.Bitmap)
                if ($playProbe.Found -and $playProbe.PixelCount -ge 40) {
                    $playProbe = [HangarPlayVision]::RefinePlayTriangleClick($cap.Bitmap, $playProbe)
                }
                $px = [int]$playProbe.CentroidX
                $py = [int]$playProbe.CentroidY
                $w = [double]$cap.Bitmap.Width
                $h = [double]$cap.Bitmap.Height
                $inLeftCard = ($playProbe.Found -and $playProbe.PixelCount -ge 40 `
                    -and ($px / $w) -ge 0.12 -and ($px / $w) -le 0.32 `
                    -and ($py / $h) -ge 0.30 -and ($py / $h) -le 0.50)
                $onWhite = $false
                if ($inLeftCard -and $px -lt $cap.Bitmap.Width -and $py -lt $cap.Bitmap.Height) {
                    $c = $cap.Bitmap.GetPixel($px, $py)
                    $onWhite = ($c.R -ge 210 -and $c.G -ge 210 -and $c.B -ge 210)
                }
                if ($inLeftCard -and $onWhite) {
                    Write-Hangar ("already_on_replays titlePx=$($replaysTitle.PixelCount) playPx=$($playProbe.PixelCount) at=$px,$py")
                    $script:AlreadyOnReplays = $true
                    if ($ScreenshotPath) { [void][HangarPlayVision]::SaveBitmap($cap.Bitmap, $ScreenshotPath) }
                    return $true
                }
            }

            if ($battle.Found -and $battle.PixelCount -ge $MinBattleOrange) {
                $stable++
                Write-Hangar ("battle_candidate px=$($battle.PixelCount) stable=$stable/$StableBattleSamples")
                if ($stable -ge $StableBattleSamples) {
                    Write-Hangar 'hangar_ready'
                    $script:AlreadyOnReplays = $false
                    if ($ScreenshotPath) { [void][HangarPlayVision]::SaveBitmap($cap.Bitmap, $ScreenshotPath) }
                    return $true
                }
            }
            else {
                if ($stable -eq 0 -and (((Get-Date).Second % 4) -eq 0)) {
                    Write-Hangar ("looking battlePx=$($battle.PixelCount) titlePx=$($replaysTitle.PixelCount)")
                }
                $stable = 0
            }
        }
        finally {
            if ($cap.Bitmap) { $cap.Bitmap.Dispose() }
        }
        Start-Sleep -Milliseconds $SampleIntervalMs
    }
    Write-Hangar 'FAILED_hangar_timeout'
    return $false
}

function Get-BlitzLocalTime([string]$Line) {
    # Prefer the stamped game-local clock: "... [info] 18:40:19 -5 [ui] ..."
    if ($Line -match '\[(?:info|error|warning)\]\s+(\d{2}:\d{2}:\d{2})\s+-?\d+') { return $Matches[1] }
    if ($Line -match '\[info\]\s+(\d{2}:\d{2}:\d{2})') { return $Matches[1] }
    return $null
}

function Test-StartReplayMarker([datetime]$Since, [int64]$LogCursor = 0) {
    $dava = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
    $log = Get-ChildItem -LiteralPath $dava -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $log) { return $false }
    if ($log.LastWriteTime -lt $Since.AddSeconds(-15)) { return $false }
    try {
        $fs = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $start = [Math]::Max([int64]0, [int64]$LogCursor)
            if ($start -gt $fs.Length) { $start = 0 }
            $take = [int]($fs.Length - $start)
            if ($take -le 0) { return $false }
            if ($take -gt 256KB) {
                $start = $fs.Length - 256KB
                $take = 256KB
            }
            $fs.Seek($start, [IO.SeekOrigin]::Begin) | Out-Null
            $buf = New-Object byte[] $take
            [void]$fs.Read($buf, 0, $take)
            $chunk = [Text.Encoding]::UTF8.GetString($buf)
        }
        finally { $fs.Close() }
        if ($chunk -match 'START_REPLAY_LOCAL|Start replay event') { return $true }
    }
    catch { }
    return $false
}

function Test-ErrorDialogMarker([datetime]$Since) {
    $dava = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
    $log = Get-ChildItem -LiteralPath $dava -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $log) { return $false }
    if ($log.LastWriteTime -lt $Since.AddMinutes(-2)) { return $false }
    # Only treat ErrorDialog lines written after $Since (file may retain prior errors).
    $hits = Select-String -LiteralPath $log.FullName -Pattern 'Dialog activated: ErrorDialog' -SimpleMatch -ErrorAction SilentlyContinue
    foreach ($h in $hits) {
        if ($h.Line -match '^(\d{2}:\d{2}:\d{2})') {
            # blitz local wall clock in line; use file write time as coarse gate only.
            # Prefer: any ErrorDialog activation after our play click window by reading
            # trailing 40KB for freshness.
        }
    }
    try {
        $fs = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $len = $fs.Length
            $take = [Math]::Min($len, 48KB)
            $fs.Seek(-$take, [IO.SeekOrigin]::End) | Out-Null
            $buf = New-Object byte[] $take
            [void]$fs.Read($buf, 0, $take)
            $tail = [Text.Encoding]::UTF8.GetString($buf)
        }
        finally { $fs.Close() }
        $sinceLocal = $Since.ToString('HH:mm:ss')
        foreach ($line in ($tail -split "`n")) {
            if ($line -notmatch 'Dialog activated: ErrorDialog') { continue }
            $t = Get-BlitzLocalTime $line
            if ($t -and $t -ge $sinceLocal) { return $true }
        }
    }
    catch { }
    return $false
}

$tplProfile = $null
$tplReplays = $null
$tplPlay = $null
$script:AlreadyOnReplays = $false

try {
    $script:tplProfile = Get-Template 'profile-hex.png'
    $script:tplReplays = Get-Template 'replays-label.png'
    $script:tplPlay = Get-Template 'play-triangle-owner.png'
    if (-not $script:tplPlay) { $script:tplPlay = Get-Template 'play-triangle.png' }
    $tplProfile = $script:tplProfile
    $tplReplays = $script:tplReplays
    $tplPlay = $script:tplPlay
    Write-Hangar ("templates profile=$([bool]$tplProfile) replays=$([bool]$tplReplays) play=$([bool]$tplPlay)")

    $game = Ensure-Game
    if (-not $game) { exit 1 }

    if (-not (Wait-HangarReady)) { exit 2 }

    Start-Sleep -Milliseconds 500

    if (-not $script:AlreadyOnReplays) {
        $profileOk = Wait-AndClick -TimeoutSeconds $StepTimeoutSeconds -Label 'profile' -ForceFocus -Finder {
            param($b)
            $c = [HangarPlayVision]::FindProfileColor($b)
            if ($c.Found -and $c.PixelCount -ge 60) { return $c }
            if ($script:tplProfile) {
                return [HangarPlayVision]::MatchTemplate($b, $script:tplProfile, 0.00, 0.18, 0.00, 0.20, 55.0)
            }
            return $c
        }
        if (-not $profileOk) { exit 3 }

        Start-Sleep -Milliseconds 1100

        $replaysOk = Wait-AndClick -TimeoutSeconds $StepTimeoutSeconds -Label 'replays' -ForceFocus -Finder {
            param($b)
            # Prefer template — white-pixel centroid hits shop/gold chrome on the profile sheet.
            if ($script:tplReplays) {
                $m = [HangarPlayVision]::MatchTemplate($b, $script:tplReplays, 0.55, 0.995, 0.02, 0.30, 42.0)
                if ($m.Found) { return $m }
            }
            $c = [HangarPlayVision]::FindReplaysColor($b)
            if ($c.Found -and $c.PixelCount -ge 200 -and $c.CentroidX -ge [int]($b.Width * 0.75)) { return $c }
            return $null
        }
        if (-not $replaysOk) { exit 3 }

        Start-Sleep -Milliseconds 2000
    }
    else {
        Write-Hangar 'skip_profile_replays_already_on_list'
        Start-Sleep -Milliseconds 800
    }

    # Let LATEST thumbnails finish painting before measuring the play glyph.
    Start-Sleep -Milliseconds 1200

    $gShot = Get-GameWindow
    if ($gShot -and $ScreenshotPath) {
        $capShot = Invoke-Capture $gShot.MainWindowHandle
        if ($capShot) {
            try {
                $listShot = Join-Path $env:TEMP 'wotb-hangar-replays-list.png'
                [void][HangarPlayVision]::SaveBitmap($capShot.Bitmap, $listShot)
                [void][HangarPlayVision]::SaveBitmap($capShot.Bitmap, $ScreenshotPath)
                Write-Hangar 'saved_replays_list_shot'
            }
            finally { $capShot.Bitmap.Dispose() }
        }
    }

    $since = Get-Date
    $playOk = $false
    $g = Get-GameWindow
    if (-not $g) { exit 1 }
    # Byte cursor so we only count Start replay written after this play attempt.
    $logCursor = 0L
    $davaPre = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
    $logPre = Get-ChildItem -LiteralPath $davaPre -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($logPre) { $logCursor = [int64]$logPre.Length }

    $cap = Invoke-Capture $g.MainWindowHandle
    if (-not $cap) { exit 3 }
    try {
        # Calibrated on .build/latest-replays-list.png (1040x807):
        # white triangle mass ~217,313 (RGB 255); template alone hit 326,318 (dark miss).
        $playBlob = [HangarPlayVision]::FindPlayColor($cap.Bitmap)
        if (-not $playBlob.Found -or $playBlob.PixelCount -lt 40) {
            $playBlob = [HangarPlayVision]::FindPlayColorWide($cap.Bitmap)
        }
        if ($playBlob.Found) {
            $playBlob = [HangarPlayVision]::RefinePlayTriangleClick($cap.Bitmap, $playBlob)
        }
        # Template only as a seed — must refine onto white pixels.
        if ($script:tplPlay) {
            $tmpl = [HangarPlayVision]::MatchTemplate($cap.Bitmap, $script:tplPlay, 0.05, 0.50, 0.25, 0.55, 40.0)
            if ($tmpl.Found) {
                $refined = [HangarPlayVision]::RefinePlayTriangleClick($cap.Bitmap, $tmpl)
                if ($refined.Found -and $refined.PixelCount -ge 20) {
                    if (-not $playBlob.Found -or $refined.PixelCount -ge $playBlob.PixelCount) {
                        $playBlob = $refined
                        Write-Hangar ("play_from_template_refined whitePx=$($refined.PixelCount)")
                    }
                }
            }
        }

        $bx = 0; $by = 0
        if ($playBlob.Found -and $playBlob.PixelCount -ge 20) {
            $bx = [int]$playBlob.CentroidX
            $by = [int]$playBlob.CentroidY
        }
        # First LATEST card play is left-of-center (~0.21x on 1040px). Reject header/title whites.
        $inLeftCard = ($bx -gt 0 -and ($bx / [double]$cap.Bitmap.Width) -ge 0.12 -and ($bx / [double]$cap.Bitmap.Width) -le 0.32 `
            -and ($by / [double]$cap.Bitmap.Height) -ge 0.30 -and ($by / [double]$cap.Bitmap.Height) -le 0.50)
        $pixOk = $false
        if ($inLeftCard -and $bx -lt $cap.Bitmap.Width -and $by -lt $cap.Bitmap.Height) {
            $p = $cap.Bitmap.GetPixel($bx, $by)
            $pixOk = ($p.R -ge 210 -and $p.G -ge 210 -and $p.B -ge 210)
            Write-Hangar ("play_target client=$bx,$by rgb=$($p.R),$($p.G),$($p.B) whitePx=$($playBlob.PixelCount) onWhite=$pixOk leftCard=$inLeftCard")
        }
        elseif ($playBlob.Found) {
            Write-Hangar ("REJECT_play_outside_left_card client=$bx,$by")
        }
        if (-not $pixOk) {
            Write-Hangar 'FAILED_play_target_not_on_white_triangle'
        }
        else {
            try { [HangarPlayVision]::ForceForeground($g.MainWindowHandle) } catch { }
            Start-Sleep -Milliseconds 300
            $sx = $cap.Rect.Left + $bx
            $sy = $cap.Rect.Top + $by
            # One deliberate screen click on the white triangle. Extra clicks /
            # SendMessage previously deactivated ReplayList without START_REPLAY.
            [void][HangarPlayVision]::SetCursorPos($sx, $sy)
            Start-Sleep -Milliseconds 500
            [HangarPlayVision]::ClickScreen($sx, $sy)
            Write-Hangar ("click_play_triangle screen=$sx,$sy client=$bx,$by")
            $playOk = $true
        }
    }
    finally { $cap.Bitmap.Dispose() }

    if (-not $playOk) { exit 3 }

    Start-Sleep -Milliseconds 1000
    # Confirm something reacted — use mtime-safe marker check (blitz clock ≠ Windows local).
    if (Test-StartReplayMarker -Since $since -LogCursor $logCursor) {
        Write-Hangar 'OK START_REPLAY_after_play_click'
        exit 0
    }
    $reacted = $false
    $dava = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
    $log = Get-ChildItem -LiteralPath $dava -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log -and $log.LastWriteTime -ge $since.AddSeconds(-15)) {
        if (Select-String -LiteralPath $log.FullName -Pattern 'LoginOnReplayDialog|START_REPLAY_LOCAL|Start replay event' -Quiet) {
            Write-Hangar 'play_click_reacted'
            $reacted = $true
        }
    }
    if (-not $reacted) {
        Write-Hangar 'WARN_no_blitz_reaction_yet_continuing'
    }

    # Keep $since from play click — do not reset (would miss START_REPLAY_LOCAL).

    # Only chain WATCH OFFLINE after LoginOnReplayDialog actually appears.
    if (-not $SkipWatchOfflineChain) {
        Write-Hangar 'wait_login_on_replay_dialog'
        $dialogDeadline = (Get-Date).AddSeconds(15)
        $sawLogin = $false
        while ((Get-Date) -lt $dialogDeadline) {
            if (Test-StartReplayMarker -Since $since -LogCursor $logCursor) {
                Write-Hangar 'OK START_REPLAY_LOCAL_no_dialog'
                exit 0
            }
            $dava = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
            $log = Get-ChildItem -LiteralPath $dava -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($log) {
                try {
                    $fs = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                    try {
                        $take = [Math]::Min($fs.Length, 48KB)
                        $fs.Seek(-$take, [IO.SeekOrigin]::End) | Out-Null
                        $buf = New-Object byte[] $take
                        [void]$fs.Read($buf, 0, $take)
                        $tail = [Text.Encoding]::UTF8.GetString($buf)
                    }
                    finally { $fs.Close() }
                    $sinceLocal = $since.ToString('HH:mm:ss')
                    foreach ($line in ($tail -split "`n")) {
                        if ($line -notmatch 'Dialog activated: LoginOnReplayDialog') { continue }
                        $t = Get-BlitzLocalTime $line
                        if ($t -and $t -ge $sinceLocal) { $sawLogin = $true; break }
                    }
                }
                catch { }
            }
            if ($sawLogin) { break }
            Start-Sleep -Milliseconds 250
        }

        if ($sawLogin) {
            Write-Hangar 'chain_click-watch-offline'
            $watchScript = Join-Path $scriptDir 'click-watch-offline.ps1'
            $watchOut = Join-Path $env:TEMP 'hangar-watch-offline.out.log'
            $watchErr = Join-Path $env:TEMP 'hangar-watch-offline.err.log'
            Remove-Item -LiteralPath $watchOut, $watchErr -Force -ErrorAction SilentlyContinue
            $watchArgs = @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', $watchScript,
                '-TimeoutSeconds', '90',
                '-MaxDialogLifetimeSeconds', '11',
                '-MinDialogAgeSeconds', '5',
                '-SyncGraceSeconds', '5'
            )
            # Prefer visual-only success when Host has no managed correlation.
            $watchHelp = Get-Content -LiteralPath $watchScript -TotalCount 80 -ErrorAction SilentlyContinue
            if ($watchHelp -match 'VisualDismissOnly') { $watchArgs += '-VisualDismissOnly' }
            $watchProc = Start-Process -FilePath powershell.exe -ArgumentList $watchArgs -Wait -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput $watchOut -RedirectStandardError $watchErr
            if (Test-Path -LiteralPath $watchOut) {
                Get-Content -LiteralPath $watchOut | ForEach-Object { Write-Host $_ }
            }
            Write-Hangar ("watch_offline_exit=" + [int]$watchProc.ExitCode)
            if (([int]$watchProc.ExitCode) -eq 0 -or (Test-StartReplayMarker -Since $since -LogCursor $logCursor)) {
                Write-Hangar 'OK via_watch_offline_chain'
                exit 0
            }
        }
        else {
            Write-Hangar 'no_login_on_replay_dialog_after_play'
        }
    }

    Write-Hangar ("confirm_playback timeout=${ConfirmTimeoutSeconds}s")
    $confirmDeadline = (Get-Date).AddSeconds($ConfirmTimeoutSeconds)
    while ((Get-Date) -lt $confirmDeadline) {
        if (Test-StartReplayMarker -Since $since -LogCursor $logCursor) {
            Write-Hangar 'OK START_REPLAY_LOCAL'
            exit 0
        }
        if (Test-ErrorDialogMarker -Since $since) {
            $g = Get-GameWindow
            if ($g) {
                $cap = Invoke-Capture $g.MainWindowHandle
                if ($cap) {
                    try {
                        $aff = [HangarPlayVision]::FindAffirmative($cap.Bitmap)
                        if ($aff.Found -and $aff.PixelCount -ge 1800) {
                            Invoke-ClickBlob $cap $aff 'affirmative'
                        }
                    }
                    finally { $cap.Bitmap.Dispose() }
                }
            }
            Write-Hangar 'FAILED_error_dialog_after_play'
            exit 5
        }
        Start-Sleep -Milliseconds 500
    }

    Write-Hangar 'FAILED_no_start_replay_marker'
    exit 5
}
catch {
    Write-Hangar ("FAILED_unexpected=" + $_.Exception.Message)
    exit 4
}
finally {
    foreach ($img in @($tplProfile, $tplReplays, $tplPlay)) {
        if ($img) { try { $img.Dispose() } catch { } }
    }
}
