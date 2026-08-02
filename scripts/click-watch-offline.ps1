#Requires -Version 5.1
<#
.SYNOPSIS
  Dismiss WoT Blitz "WATCH OFFLINE" via orange-button blob find + dual verify.

.DESCRIPTION
  Specs:
    docs/superpowers/specs/2026-08-02-watch-offline-color-blob.md
    docs/superpowers/specs/2026-08-02-watch-offline-sync-ready-gate.md

  Never clicks LOG IN AND WATCH (green, right). Waits until the dialog passes the
  sync-dim ready gate (bright + strong orange, optionally after sync dim observed
  or grace elapsed), holds briefly so the dialog can accept input, then clicks
  its centroid. Requires both:
    - GET /api/v1/game/state â†’ OfflineReplayVerified
    - Post-click orange blob area below dismiss threshold (dialog gone)

.EXITCODES
  0  Dual success (gate + dialog dismissed)
  1  Game window missing
  2  Rendezvous / capability missing
  3  Retries exhausted (gate and/or dialog check failed)
  4  Unexpected error
  5  Ready gate never satisfied (dialog not interactive in time)
  6  Host already Denied (stale lifecycle timeout) â€” restart via launch-offline-replay-for-od.ps1
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 90,
    [int]$MaxRounds = 5,
    [string]$ScreenshotPath = $(Join-Path $env:TEMP 'wotb-watch-offline-verify.png'),
    [int]$MinBlobPixels = 400,
    [int]$DismissMaxPixels = 120,
    # Visual ready gate: sync-dim state machine (see sync-ready-gate spec).
    [int]$AppearTimeoutSeconds = 25,
    [int]$ReadyTimeoutSeconds = 35,
    # After SeenSyncing: 1 sample. Grace path: also 1.
    [int]$StableSamples = 1,
    [int]$SampleIntervalMs = 150,
    [int]$ReadyHoldSeconds = 0,
    [int]$SyncMaxLuminance = 40,
    [int]$SyncMaxOrange = 400,
    [int]$ReadyMinOrange = 2000,
    [int]$ReadyMinLuminance = 45,
    # Bright without sync: wait this long after first bright before grace click.
    # Live blitz-logs: Start replay ~8-9s after LoginOnReplayDialog; clicks at
    # ~2-3s deactivate the dialog with no Start replay; ErrorDialog ~11-13s.
    [int]$SyncGraceSeconds = 5,
    # Never grace-click before the dialog has lived this long (post-sync path
    # ignores this once SeenSyncing was observed).
    [int]$MinDialogAgeSeconds = 5,
    # Hard ceiling from first dialog sighting to click (beat Error 126).
    # Raised: sync can start ~5s after bright and last ~2s; ErrorDialog ~18s.
    [int]$MaxDialogLifetimeSeconds = 16,
    # When set, write the exit code here and throw WATCH_EXIT:<code> so the
    # launcher can invoke this script in-process (no nested console focus steal).
    [string]$ResultPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Quit-WatchOffline([int]$Code) {
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        Set-Content -LiteralPath $ResultPath -Value "$Code" -Encoding ascii -NoNewline
        throw "WATCH_EXIT:$Code"
    }
    exit $Code
}

if (-not ('WatchOfflineVision' -as [type])) {
Add-Type @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WatchOfflineVision {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

  public static void ForceForeground(IntPtr hWnd) {
    EnsureDpiAware();
    uint unused;
    uint target = GetWindowThreadProcessId(hWnd, out unused);
    uint current = GetCurrentThreadId();
    IntPtr fg = GetForegroundWindow();
    uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out unused) : 0;
    if (fgThread != 0) AttachThreadInput(current, fgThread, true);
    if (target != 0) AttachThreadInput(current, target, true);
    ShowWindow(hWnd, 9);
    SetForegroundWindow(hWnd);
    if (target != 0) AttachThreadInput(current, target, false);
    if (fgThread != 0) AttachThreadInput(current, fgThread, false);
  }

  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint PW_RENDERFULLCONTENT = 0x00000002;
  static bool _dpiAware;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public struct BlobHit {
    public bool Found;
    public int PixelCount;
    public int CentroidX; // client/bitmap coords
    public int CentroidY;
    public int MinX, MinY, MaxX, MaxY;
  }

  public struct DialogAnalysis {
    public BlobHit Blob;
    public double DialogMeanLuminance;
  }

  public static void EnsureDpiAware() {
    if (_dpiAware) return;
    try { SetProcessDPIAware(); } catch { }
    _dpiAware = true;
  }

  public static void ClickScreen(int x, int y) {
    EnsureDpiAware();
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(100);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(70);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
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

  static double Rec709Luminance(byte r, byte g, byte b) {
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  }

  public static DialogAnalysis AnalyzeDialog(Bitmap bmp) {
    var result = new DialogAnalysis();
    if (bmp == null) return result;

    int w = bmp.Width, h = bmp.Height;
    // Orange search: left/center band (avoid green LOG IN on the right).
    int orangeX0 = (int)(w * 0.18);
    int orangeX1 = (int)(w * 0.55);
    int orangeY0 = (int)(h * 0.40);
    int orangeY1 = (int)(h * 0.70);
    // Luminance: wider modal ROI.
    int lumX0 = (int)(w * 0.25);
    int lumX1 = (int)(w * 0.75);
    int lumY0 = (int)(h * 0.35);
    int lumY1 = (int)(h * 0.70);

    long sumX = 0, sumY = 0;
    int orangeCount = 0;
    int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
    double lumSum = 0;
    int lumCount = 0;

    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try {
      int stride = data.Stride;
      IntPtr scan0 = data.Scan0;
      byte[] row = new byte[Math.Abs(stride)];
      int yStart = Math.Min(orangeY0, lumY0);
      int yEnd = Math.Max(orangeY1, lumY1);
      for (int y = yStart; y < yEnd; y++) {
        Marshal.Copy(IntPtr.Add(scan0, y * stride), row, 0, row.Length);
        for (int x = 0; x < w; x++) {
          int i = x * 4;
          byte bb = row[i], gg = row[i + 1], rr = row[i + 2];

          if (y >= lumY0 && y < lumY1 && x >= lumX0 && x < lumX1) {
            lumSum += Rec709Luminance(rr, gg, bb);
            lumCount++;
          }

          if (y >= orangeY0 && y < orangeY1 && x >= orangeX0 && x < orangeX1) {
            if (!IsWatchOfflineOrange(rr, gg, bb)) continue;
            orangeCount++;
            sumX += x;
            sumY += y;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
          }
        }
      }
    } finally {
      bmp.UnlockBits(data);
    }

    result.Blob.PixelCount = orangeCount;
    if (orangeCount > 0) {
      result.Blob.Found = true;
      result.Blob.CentroidX = (int)(sumX / orangeCount);
      result.Blob.CentroidY = (int)(sumY / orangeCount);
      result.Blob.MinX = minX; result.Blob.MinY = minY;
      result.Blob.MaxX = maxX; result.Blob.MaxY = maxY;
    }
    result.DialogMeanLuminance = lumCount > 0 ? lumSum / lumCount : 0.0;
    return result;
  }

  // Back-compat wrapper for callers expecting blob-only analysis.
  public static BlobHit FindOrangeBlob(Bitmap bmp) {
    return AnalyzeDialog(bmp).Blob;
  }
}
"@ -ReferencedAssemblies System.Drawing.dll
}

function Get-Rendezvous {
    $dir = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
    $file = Get-ChildItem $dir -File -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    return (Get-Content $file.FullName -Raw | ConvertFrom-Json)
}

function Get-ApiContext {
    # Re-read every call: rendezvous capability rotates (~5 min) and 401s mid-wait.
    $rv = Get-Rendezvous
    return @{
        Base    = [string]$rv.baseUri
        Headers = @{
            'X-WotBTreader-Capability' = "$($rv.capability)"
            'Content-Type'             = 'application/json'
        }
    }
}

function Get-GameState {
    $api = Get-ApiContext
    try {
        return Invoke-RestMethod -Uri "$($api.Base)/api/v1/game/state" -Headers $api.Headers
    }
    catch {
        Write-Host ("watch_offline: state_http_error=" + $_.Exception.Message)
        return $null
    }
}

function Get-VerificationState {
    $state = Get-GameState
    if (-not $state) { return 'Unknown' }
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
        $dialog = [WatchOfflineVision]::AnalyzeDialog($bmp)
        if ($SavePath) { [void][WatchOfflineVision]::SaveBitmap($bmp, $SavePath) }
        return [pscustomobject]@{
            Rect               = $rect
            Blob               = $dialog.Blob
            DialogMeanLuminance = [double]$dialog.DialogMeanLuminance
            Width              = $bmp.Width
            Height             = $bmp.Height
        }
    }
    finally {
        $bmp.Dispose()
    }
}

function Test-DialogPresent([int]$OrangePx, [double]$DialogMeanL) {
    # First dialog sighting: orange blob (incl. dim sync ~63 px) or dim modal luminance.
    if ($OrangePx -ge 50) { return $true }
    if ($DialogMeanL -lt $SyncMaxLuminance -and $DialogMeanL -gt 15) { return $true }
    return $false
}

function Test-ReadySample(
    [int]$OrangePx,
    [double]$DialogMeanL,
    [bool]$SeenSyncing,
    [Nullable[datetime]]$FirstBrightAt,
    [Nullable[datetime]]$FirstDialogAt
) {
    if ($OrangePx -lt $ReadyMinOrange) { return $false }
    if ($DialogMeanL -lt $ReadyMinLuminance) { return $false }
    # Prefer post-sync: first bright after dim is the interactive window.
    if ($SeenSyncing) { return $true }
    # Grace: bright idle without observed sync â€” must clear both age floors so
    # we do not click at ~2â€“3s (dialog dismisses, no Start replay in blitz-log).
    if (-not $FirstBrightAt -or -not $FirstDialogAt) { return $false }
    $brightAge = ((Get-Date) - $FirstBrightAt).TotalSeconds
    $dialogAge = ((Get-Date) - $FirstDialogAt).TotalSeconds
    if ($brightAge -ge $SyncGraceSeconds -and $dialogAge -ge $MinDialogAgeSeconds) {
        return $true
    }
    return $false
}

try {
    [void][WatchOfflineVision]::EnsureDpiAware()
    try {
        $null = Get-Rendezvous
    }
    catch {
        Write-Host 'watch_offline: rendezvous_missing'
        Quit-WatchOffline 2
    }

    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'watch_offline: no_game_window'
        Quit-WatchOffline 1
    }

    $beforeState = Get-GameState
    $before = if ($beforeState -and $beforeState.verificationState) {
        [string]$beforeState.verificationState
    }
    else { Get-VerificationState }
    $beforeReason = if ($beforeState -and $beforeState.reasonCode) {
        [string]$beforeState.reasonCode
    }
    else { '' }
    Write-Host "watch_offline: before=$before reason=$beforeReason pid=$($game.Id)"

    if ($before -eq 'Denied') {
        Write-Host 'watch_offline: FAILED_host_denied (do not click; run scripts/launch-offline-replay-for-od.ps1)'
        Quit-WatchOffline 6
    }

    [void][WatchOfflineVision]::ShowWindow($game.MainWindowHandle, 9)
    [void][WatchOfflineVision]::SetForegroundWindow($game.MainWindowHandle)
    Start-Sleep -Milliseconds 300

    # --- Sync-dim ready gate ---
    $phase = 'LookingForDialog'
    $appearDeadline = (Get-Date).AddSeconds($AppearTimeoutSeconds)
    $readyDeadline = $null
    $firstDialogAt = $null
    $firstBrightAt = $null
    $seenSyncing = $false
    $stable = 0
    $readyAnalysis = $null
    Write-Host ("watch_offline: ready_gate appear={0}s ready={1}s brightGrace={2}s minDialogAge={3}s hold={4}s cap={5}s" -f `
        $AppearTimeoutSeconds, $ReadyTimeoutSeconds, $SyncGraceSeconds, $MinDialogAgeSeconds, `
        $ReadyHoldSeconds, $MaxDialogLifetimeSeconds)

    $lastFocusAt = [datetime]::MinValue
    while ($true) {
        $game = Get-GameWindow
        if (-not $game) {
            Write-Host 'watch_offline: no_game_window_while_waiting'
            Quit-WatchOffline 1
        }
        # Do not spam ShowWindow/SetForeground during splash â€” live logs show
        # OnBackground â†’ WindowDestroyed within ~1s when focus-churned early.
        $shouldFocus = ($phase -ne 'LookingForDialog') -or `
            (((Get-Date) - $lastFocusAt).TotalSeconds -ge 3)
        if ($shouldFocus) {
            [void][WatchOfflineVision]::ShowWindow($game.MainWindowHandle, 9)
            [void][WatchOfflineVision]::SetForegroundWindow($game.MainWindowHandle)
            $lastFocusAt = Get-Date
        }

        $analysis = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
        if (-not $analysis) {
            Write-Host 'watch_offline: capture_failed_while_waiting'
            Start-Sleep -Milliseconds $SampleIntervalMs
            continue
        }

        $orangePx = [int]$analysis.Blob.PixelCount
        $dialogMeanL = [Math]::Round([double]$analysis.DialogMeanLuminance, 1)

        if ($phase -eq 'LookingForDialog') {
            if (Test-DialogPresent $orangePx $dialogMeanL) {
                $phase = 'WaitingForReady'
                $firstDialogAt = Get-Date
                $readyDeadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
                Write-Host ("watch_offline: phase={0} dialogMeanL={1} orangePx={2} seenSync={3} stable={4}" -f `
                    $phase, $dialogMeanL, $orangePx, $seenSyncing, $stable)
            }
            elseif ((Get-Date) -ge $appearDeadline) {
                break
            }
            else {
                Write-Host ("watch_offline: phase={0} dialogMeanL={1} orangePx={2} seenSync={3} stable={4}" -f `
                    $phase, $dialogMeanL, $orangePx, $seenSyncing, $stable)
            }
        }
        else {
            if ((Get-Date) -ge $readyDeadline) {
                break
            }

            # Owner sync: dim dialog (~31 L) with collapsed-but-nonzero orange (~60â€“80).
            # Blank frames (Lâ‰ˆ0) and bright low-orange splash must NOT arm SeenSyncing.
            $looksSyncing = (
                $dialogMeanL -gt 18 -and
                $dialogMeanL -lt $SyncMaxLuminance -and
                $orangePx -ge 30 -and
                $orangePx -lt $SyncMaxOrange
            )
            if ($looksSyncing) {
                $seenSyncing = $true
                $firstBrightAt = $null
                Write-Host ("watch_offline: syncing_observed dialogMeanL={0} orangePx={1}" -f $dialogMeanL, $orangePx)
            }
            elseif ($orangePx -ge $ReadyMinOrange -and $dialogMeanL -ge $ReadyMinLuminance) {
                if (-not $firstBrightAt) {
                    $firstBrightAt = Get-Date
                    Write-Host 'watch_offline: first_bright_ready_looking'
                }
            }

            # After sync clears, click on the first bright+strong frame.
            $needStable = if ($seenSyncing) { 1 } else { $StableSamples }
            if (Test-ReadySample $orangePx $dialogMeanL $seenSyncing $firstBrightAt $firstDialogAt) {
                $stable++
                $readyAnalysis = $analysis
                Write-Host ("watch_offline: phase={0} dialogMeanL={1} orangePx={2} seenSync={3} stable={4}/{5}" -f `
                    $phase, $dialogMeanL, $orangePx, $seenSyncing, $stable, $needStable)
                if ($stable -ge $needStable) {
                    break
                }
            }
            else {
                if ($stable -gt 0) {
                    Write-Host ("watch_offline: phase={0} dialogMeanL={1} orangePx={2} seenSync={3} stable=0_reset" -f `
                        $phase, $dialogMeanL, $orangePx, $seenSyncing)
                }
                else {
                    Write-Host ("watch_offline: phase={0} dialogMeanL={1} orangePx={2} seenSync={3} stable={4}" -f `
                        $phase, $dialogMeanL, $orangePx, $seenSyncing, $stable)
                }
                $stable = 0
                $readyAnalysis = $null
            }
        }

        if ($firstDialogAt -and ((Get-Date) - $firstDialogAt).TotalSeconds -ge $MaxDialogLifetimeSeconds) {
            Write-Host ("watch_offline: dialog_lifetime_cap_{0}s (avoid Error 126)" -f $MaxDialogLifetimeSeconds)
            break
        }

        Start-Sleep -Milliseconds $SampleIntervalMs
    }

    $needStableFinal = if ($seenSyncing) { 1 } else { $StableSamples }
    if ($stable -lt $needStableFinal -or -not $readyAnalysis) {
        $vsNow = Get-VerificationState
        if ($vsNow -eq 'OfflineReplayVerified') {
            Write-Host 'watch_offline: already_verified_no_dialog'
            Quit-WatchOffline 0
        }
        Write-Host 'watch_offline: FAILED_ready_never_reached'
        Quit-WatchOffline 5
    }

    $preCount = [int]$readyAnalysis.Blob.PixelCount
    # Brief settle after sync recovery so the CTA accepts input; still short vs Error 126.
    $holdMs = if ($seenSyncing) { 350 } else { [int]($ReadyHoldSeconds * 1000) }
    Write-Host ("watch_offline: phase=Ready dialogMeanL={0} orangePx={1} seenSync={2} stable={3} hold_{4}ms" -f `
        [Math]::Round([double]$readyAnalysis.DialogMeanLuminance, 1), $preCount, $seenSyncing, $stable, $holdMs)
    if ($holdMs -gt 0) {
        Start-Sleep -Milliseconds $holdMs
    }

    # Re-check after hold â€” dialog may have timed out during hold.
    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'watch_offline: window_lost_after_hold'
        Quit-WatchOffline 1
    }
    [WatchOfflineVision]::ForceForeground($game.MainWindowHandle)
    Start-Sleep -Milliseconds 150
    $analysis = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
    if (-not $analysis) {
        Write-Host 'watch_offline: capture_failed_after_hold'
        Quit-WatchOffline 4
    }
    $preCount = [int]$analysis.Blob.PixelCount
    $cx = [int]$analysis.Blob.CentroidX
    $cy = [int]$analysis.Blob.CentroidY
    $cw = [int]$analysis.Width
    $ch = [int]$analysis.Height
    Write-Host ("watch_offline: pre_click_orange_pixels={0} found={1} centroid={2},{3} meanL={4}" -f `
        $preCount, $analysis.Blob.Found, $cx, $cy, [Math]::Round([double]$analysis.DialogMeanLuminance, 1))

    $beforeState = Get-GameState
    $before = if ($beforeState -and $beforeState.verificationState) {
        [string]$beforeState.verificationState
    }
    else { 'Unknown' }

    if ($before -eq 'OfflineReplayVerified' -and $preCount -le $DismissMaxPixels) {
        Write-Host 'watch_offline: already_dismissed_and_verified'
        Quit-WatchOffline 0
    }

    if ($preCount -lt $MinBlobPixels -and $before -ne 'OfflineReplayVerified') {
        Write-Host 'watch_offline: FAILED_blob_gone_after_hold (dialog timed out?)'
        Quit-WatchOffline 5
    }

    $cxRatio = if ($cw -gt 0) { $cx / [double]$cw } else { 1.0 }
    $cyRatio = if ($ch -gt 0) { $cy / [double]$ch } else { 1.0 }
    if ($cxRatio -lt 0.18 -or $cxRatio -gt 0.48 -or $cyRatio -lt 0.38 -or $cyRatio -gt 0.62) {
        Write-Host ("watch_offline: FAILED_centroid_outside_dialog_band cxRatio={0:N2} cyRatio={1:N2}" -f $cxRatio, $cyRatio)
        Quit-WatchOffline 5
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
            Quit-WatchOffline 1
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
            $rx = $analysis.Blob.CentroidX / [double][Math]::Max(1, $analysis.Width)
            $ry = $analysis.Blob.CentroidY / [double][Math]::Max(1, $analysis.Height)
            if ($rx -lt 0.18 -or $rx -gt 0.48 -or $ry -lt 0.38 -or $ry -gt 0.62) {
                Write-Host ("watch_offline: skip_click_outside_dialog_band cxRatio={0:N2} cyRatio={1:N2}" -f $rx, $ry)
            }
            else {
                $sawBlob = $true
                $screenX = $analysis.Rect.Left + $analysis.Blob.CentroidX
                $screenY = $analysis.Rect.Top + $analysis.Blob.CentroidY
                Write-Host ("watch_offline: click_blob screen={0},{1} client={2},{3}" -f `
                    $screenX, $screenY, $analysis.Blob.CentroidX, $analysis.Blob.CentroidY)
                [WatchOfflineVision]::ForceForeground($game.MainWindowHandle)
                Start-Sleep -Milliseconds 100
                [WatchOfflineVision]::ClickScreen($screenX, $screenY)
                Start-Sleep -Milliseconds 250
                [WatchOfflineVision]::ClickScreen($screenX + 3, $screenY + 2)
            }
        }
        elseif (-not $sawBlob) {
            Write-Host 'watch_offline: no_blob_this_round'
        }

        $pollUntil = (Get-Date).AddSeconds(8)
        if ($pollUntil -gt $deadline) { $pollUntil = $deadline }
        do {
            Start-Sleep -Seconds 1
            $pollState = Get-GameState
            $vs = if ($pollState -and $pollState.verificationState) {
                [string]$pollState.verificationState
            }
            else { 'Unknown' }
            $reason = if ($pollState -and $pollState.reasonCode) {
                [string]$pollState.reasonCode
            }
            else { '' }
            Write-Host "watch_offline: poll=$vs reason=$reason"
            if ($vs -eq 'OfflineReplayVerified') { $gateOk = $true; break }
            if ($vs -eq 'Denied') {
                Write-Host 'watch_offline: FAILED_host_denied_mid_click'
                Quit-WatchOffline 6
            }
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

        Write-Host ("watch_offline: gateOk={0} dialogGone={1}" -f $gateOk, $dialogGone)
        if ($gateOk -and $dialogGone) { break }
    }

    $after = Get-VerificationState
    $finalGame = Get-GameWindow
    if (-not $finalGame) {
        Write-Host 'watch_offline: window_lost_final'
        Quit-WatchOffline 1
    }
    $final = Get-WindowAnalysis $finalGame.MainWindowHandle $ScreenshotPath
    $finalCount = if ($final) { [int]$final.Blob.PixelCount } else { -1 }
    Write-Host ("watch_offline: after={0} final_orange_pixels={1}" -f $after, $finalCount)

    $dialogGone = $dialogGone -or ($finalCount -ge 0 -and $finalCount -le $DismissMaxPixels)
    $gateOk = $gateOk -or ($after -eq 'OfflineReplayVerified')

    if ($gateOk -and $dialogGone) {
        Write-Host 'watch_offline: SUCCESS_gate_and_dialog_dismissed'
        Quit-WatchOffline 0
    }

    if (-not $sawBlob) {
        Write-Host 'watch_offline: FAILED_no_orange_blob'
        Quit-WatchOffline 5
    }

    try {
        $dava = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
        $latestLog = Get-ChildItem -LiteralPath $dava -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($latestLog) {
            $hasStart = Select-String -LiteralPath $latestLog.FullName -Pattern 'START_REPLAY_LOCAL|Start replay event' -Quiet
            $hasErrDlg = Select-String -LiteralPath $latestLog.FullName -Pattern 'ErrorDialog' -SimpleMatch -Quiet
            $hasLogin = Select-String -LiteralPath $latestLog.FullName -Pattern 'LoginOnReplayDialog' -SimpleMatch -Quiet
            Write-Host ("watch_offline: blitz_log_markers startReplay={0} errorDialog={1} loginOnReplay={2}" -f `
                [bool]$hasStart, [bool]$hasErrDlg, [bool]$hasLogin)
        }
    }
    catch { }

    Write-Host ("watch_offline: FAILED gateOk={0} dialogGone={1}" -f $gateOk, $dialogGone)
    Quit-WatchOffline 3
}
catch {
    Write-Host ("watch_offline: error=" + $_.Exception.Message)
    Quit-WatchOffline 4
}

