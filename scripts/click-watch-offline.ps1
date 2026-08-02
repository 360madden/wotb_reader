#Requires -Version 5.1
<#
.SYNOPSIS
  Dismiss WoT Blitz "WATCH OFFLINE" via orange-button blob find + dual verify.

.DESCRIPTION
  Spec: docs/superpowers/specs/2026-08-02-watch-offline-color-blob.md

  Never clicks LOG IN AND WATCH (green, right). Finds the largest orange/amber
  blob in the left/center dialog ROI, clicks its centroid, then requires both:
    - GET /api/v1/game/state → OfflineReplayVerified
    - Post-click orange blob area below dismiss threshold (dialog gone)

.EXITCODES
  0  Dual success (gate + dialog dismissed)
  1  Game window missing
  2  Rendezvous / capability missing
  3  Retries exhausted (gate and/or dialog check failed)
  4  Unexpected error
  5  Dialog orange blob never found (cannot aim)
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 60,
    [int]$MaxRounds = 5,
    [string]$ScreenshotPath = $(Join-Path $env:TEMP 'wotb-watch-offline-verify.png'),
    [int]$MinBlobPixels = 400,
    [int]$DismissMaxPixels = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WatchOfflineVision {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint PW_RENDERFULLCONTENT = 0x00000002;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public struct BlobHit {
    public bool Found;
    public int PixelCount;
    public int CentroidX; // client/bitmap coords
    public int CentroidY;
    public int MinX, MinY, MaxX, MaxY;
  }

  public static void ClickScreen(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(50);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
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
    if (bmp == null) return false;
    bmp.Save(path, ImageFormat.Png);
    return true;
  }

  static bool IsWatchOfflineOrange(byte r, byte g, byte b) {
    // Amber/orange CTA; excludes green LOG IN (g-dominant) and grey CANCEL.
    if (r < 170) return false;
    if (g < 70 || g > 210) return false;
    if (b > 110) return false;
    if (r < g + 25) return false;   // not greenish
    if (r < b + 80) return false;   // strongly red-over-blue
    return true;
  }

  public static BlobHit FindOrangeBlob(Bitmap bmp) {
    var hit = new BlobHit();
    if (bmp == null) return hit;

    int w = bmp.Width, h = bmp.Height;
    int x0 = (int)(w * 0.18);
    int x1 = (int)(w * 0.55);
    int y0 = (int)(h * 0.40);
    int y1 = (int)(h * 0.70);

    long sumX = 0, sumY = 0;
    int count = 0;
    int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;

    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      IntPtr scan0 = data.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      for (int y = y0; y < y1; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int x = x0; x < x1; x++) {
          int i = x * 4;
          byte bb = row[i], gg = row[i + 1], rr = row[i + 2];
          if (!IsWatchOfflineOrange(rr, gg, bb)) continue;
          count++;
          sumX += x;
          sumY += y;
          if (x < minX) minX = x;
          if (y < minY) minY = y;
          if (x > maxX) maxX = x;
          if (y > maxY) maxY = y;
        }
      }
    } finally {
      bmp.UnlockBits(data);
    }

    hit.PixelCount = count;
    if (count <= 0) return hit;
    hit.Found = true;
    hit.CentroidX = (int)(sumX / count);
    hit.CentroidY = (int)(sumY / count);
    hit.MinX = minX; hit.MinY = minY; hit.MaxX = maxX; hit.MaxY = maxY;
    return hit;
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
    return Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
        Select-Object -First 1
}

function Get-WindowAnalysis([IntPtr]$Hwnd, [string]$SavePath) {
    $rect = New-Object WatchOfflineVision+RECT
    $bmp = [WatchOfflineVision]::CaptureBitmap($Hwnd, [ref]$rect)
    if (-not $bmp) { return $null }
    try {
        $blob = [WatchOfflineVision]::FindOrangeBlob($bmp)
        if ($SavePath) { [void][WatchOfflineVision]::SaveBitmap($bmp, $SavePath) }
        return [pscustomobject]@{
            Rect     = $rect
            Blob     = $blob
            Width    = $bmp.Width
            Height   = $bmp.Height
        }
    }
    finally {
        $bmp.Dispose()
    }
}

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

    [void][WatchOfflineVision]::ShowWindow($game.MainWindowHandle, 9)
    [void][WatchOfflineVision]::SetForegroundWindow($game.MainWindowHandle)
    Start-Sleep -Milliseconds 300

    $analysis = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
    if (-not $analysis) {
        Write-Host 'watch_offline: capture_failed'
        exit 4
    }
    $preCount = [int]$analysis.Blob.PixelCount
    Write-Host ("watch_offline: pre_orange_pixels={0} found={1} centroid={2},{3}" -f `
        $preCount, $analysis.Blob.Found, $analysis.Blob.CentroidX, $analysis.Blob.CentroidY)

    if ($before -eq 'OfflineReplayVerified' -and $preCount -le $DismissMaxPixels) {
        Write-Host 'watch_offline: already_dismissed_and_verified'
        Write-Host "watch_offline: screenshot=$ScreenshotPath"
        exit 0
    }

    if ($preCount -lt $MinBlobPixels -and $before -ne 'OfflineReplayVerified') {
        Write-Host 'watch_offline: FAILED_no_orange_blob (dialog not visible or capture blank)'
        Write-Host "watch_offline: screenshot=$ScreenshotPath"
        exit 5
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $sawBlob = $preCount -ge $MinBlobPixels
    $gateOk = $false
    $dialogGone = $false

    for ($round = 1; $round -le $MaxRounds; $round++) {
        if ((Get-Date) -ge $deadline) { break }

        $game = Get-GameWindow
        if (-not $game) {
            Write-Host 'watch_offline: window_lost'
            exit 1
        }

        [void][WatchOfflineVision]::ShowWindow($game.MainWindowHandle, 9)
        [void][WatchOfflineVision]::SetForegroundWindow($game.MainWindowHandle)
        Start-Sleep -Milliseconds 250

        $analysis = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
        if (-not $analysis) {
            Write-Host 'watch_offline: capture_failed_round'
            continue
        }

        $count = [int]$analysis.Blob.PixelCount
        Write-Host ("watch_offline: round={0} orange_pixels={1} found={2}" -f $round, $count, $analysis.Blob.Found)

        if ($count -ge $MinBlobPixels) {
            $sawBlob = $true
            $screenX = $analysis.Rect.Left + $analysis.Blob.CentroidX
            $screenY = $analysis.Rect.Top + $analysis.Blob.CentroidY
            Write-Host ("watch_offline: click_blob screen={0},{1} client={2},{3}" -f `
                $screenX, $screenY, $analysis.Blob.CentroidX, $analysis.Blob.CentroidY)
            [WatchOfflineVision]::ClickScreen($screenX, $screenY)
            Start-Sleep -Milliseconds 400
            # Single confirming click near centroid (small jitter)
            [WatchOfflineVision]::ClickScreen($screenX + 3, $screenY + 2)
        }
        elseif (-not $sawBlob) {
            Write-Host 'watch_offline: no_blob_this_round'
        }

        $pollUntil = (Get-Date).AddSeconds(8)
        if ($pollUntil -gt $deadline) { $pollUntil = $deadline }
        do {
            Start-Sleep -Seconds 1
            $vs = Get-VerificationState $base $headers
            Write-Host "watch_offline: poll=$vs"
            if ($vs -eq 'OfflineReplayVerified') { $gateOk = $true; break }
        } while ((Get-Date) -lt $pollUntil)

        Start-Sleep -Milliseconds 500
        $post = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
        if ($post) {
            $postCount = [int]$post.Blob.PixelCount
            Write-Host ("watch_offline: post_orange_pixels={0}" -f $postCount)
            if ($postCount -le $DismissMaxPixels) { $dialogGone = $true }
            elseif ($sawBlob -and $preCount -gt 0 -and $postCount -lt [Math]::Max($DismissMaxPixels, [int]($preCount * 0.15))) {
                $dialogGone = $true
                Write-Host 'watch_offline: dialog_shrunk_ok'
            }
        }

        Write-Host "watch_offline: screenshot=$ScreenshotPath gateOk=$gateOk dialogGone=$dialogGone"
        if ($gateOk -and $dialogGone) { break }
    }

    $after = Get-VerificationState $base $headers
    $final = Get-WindowAnalysis (Get-GameWindow).MainWindowHandle $ScreenshotPath
    $finalCount = if ($final) { [int]$final.Blob.PixelCount } else { -1 }
    Write-Host ("watch_offline: after={0} final_orange_pixels={1}" -f $after, $finalCount)

    $dialogGone = $dialogGone -or ($finalCount -ge 0 -and $finalCount -le $DismissMaxPixels)
    $gateOk = $gateOk -or ($after -eq 'OfflineReplayVerified')

    if ($gateOk -and $dialogGone) {
        Write-Host 'watch_offline: SUCCESS_gate_and_dialog_dismissed'
        exit 0
    }

    if (-not $sawBlob) {
        Write-Host 'watch_offline: FAILED_no_orange_blob'
        exit 5
    }

    Write-Host ("watch_offline: FAILED gateOk={0} dialogGone={1}" -f $gateOk, $dialogGone)
    exit 3
}
catch {
    Write-Host ("watch_offline: error=" + $_.Exception.Message)
    exit 4
}
