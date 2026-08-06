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
  its centroid. Success requires OfflineReplayVerified, which proves the
  replay started (lifecycle monitor requires a fresh START_REPLAY_LOCAL / Start
  replay event marker) and therefore proves the dialog is gone - the orange
  dialog ROI then false-positives on replay-HUD content (OD-RECOVERY-017), so
  once verified the script stops clicking instead of chasing the blob.

  Window resize (FRESH17): the launch script may shrink the game window to
  640x360 so it never covers the operator's other programs. All absolute-pixel
  thresholds (orange counts) are scaled by the captured window's area ratio vs
  the 1920x1080 reference (-ReferenceWidth/Height), so the ready gate fires at
  any window size; luminance means are area-independent and stay absolute.

  Flake fix (OD-044): the Host lifecycle gate lags the dialog dismissal by
  ~9-10s, so the old 8s poll window expired before verification and round 2
  re-clicked the live replay HUD; the SW_RESTORE/foreground churn around that
  second click hid the window (become hidden -> OnBackground) in ~40% of
  double-clicked runs. The script now treats the blitz-log 'Start replay
  event' marker (written at dialog dismissal) as fast ground truth: once it
  appears, no further clicks are ever fired, and the Host gate is awaited
  separately so the launcher still observes OfflineReplayVerified. All
  ShowWindow(SW_RESTORE) churn was removed in favor of soft SetForegroundWindow.

.EXITCODES
  0  Dual success (gate + dialog dismissed)
  1  Game window missing
  2  Rendezvous / capability missing
  3  Retries exhausted (gate and/or dialog check failed)
  4  Unexpected error
  5  Ready gate never satisfied (dialog not interactive in time)
  6  Host already Denied (stale lifecycle timeout) aEUR" restart via launch-offline-replay-for-od.ps1
#>
# ResultPath is read by Quit-WatchOffline (a child function) via script-scope
# dynamic lookup; PSSA's PSReviewUnusedParameter cannot see cross-function
# script-parameter use and would report it as dead. NOTE: the suppression is
# file-scoped -- a genuinely dead parameter added to this script later will
# also go un-flagged; review new parameters manually.
[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'ResultPath is consumed by Quit-WatchOffline via script-scope lookup.')]
# PSAvoidUsingEmptyCatchBlock: every empty catch in this file is DELIBERATE
# and documented inline - best-effort engine probing (Add-Type / assembly
# resolution / optional probes) where a throw would kill the watch step before
# any dialog logic runs. The FRESH15 fix added five such probes so the C#
# vision helper compiles on PS 5.1 AND pwsh 7.6/.NET 10.
[System.Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingEmptyCatchBlock', '', Justification = 'Deliberate best-effort probes; each empty catch is documented inline.')]
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
    [string]$ResultPath = '',
    # FRESH16 click reliability: the ready gate fires on the FIRST bright frame
    # (StableSamples=1 post-sync), but the CTA's entrance animation can still be
    # running - clicking mid-animation misses (the button is not yet hit-testable).
    # Before the click, require the blob centroid to settle: consecutive samples
    # whose centroid moves less than SettleMaxCentroidDeltaPx. The click then
    # happens on a fully-rendered button.
    [int]$SettleSamples = 2,
    [int]$SettleMaxCentroidDeltaPx = 12,
    [int]$SettleSampleIntervalMs = 120,
    # Hover-before-press and press-hold durations for the SendInput click.
    [int]$ClickHoverMs = 200,
    [int]$ClickHoldMs = 120,
    # FRESH16: alternate the click channel per round. SendInput is the primary
    # (real-input injection); a covering window stealing the foreground makes it
    # land elsewhere, so even rounds use the PostMessage client-message channel
    # (delivers straight to the game window, immune to the foreground lock).
    [int]$MessageClickEveryRound = 2,
    # FRESH17: the launch script may resize the game window to a small footprint
    # (640x360 default) so it never covers the operator's other programs. All
    # absolute-pixel thresholds here were tuned at ~1920x1080; at 640x360 the
    # same UI yields ~1/9 the orange pixels, so the ready gate would never fire.
    # Get-WindowAnalysis computes PxScale = window area / reference area and
    # every absolute threshold is multiplied by it (floored at 5px). Luminance
    # thresholds are area-independent (means) and are NOT scaled.
    [int]$ReferenceWidth = 1920,
    [int]$ReferenceHeight = 1080,
    # Playback-only: skip Host rendezvous/gate; success = dialog dismissed
    # and/or START_REPLAY_LOCAL in blitz-log.
    [switch]$VisualDismissOnly
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

# FRESH16: renamed V2 -> V3. The autoloop retry relaunches the launch script
# IN-PROCESS in the same pwsh process, so the old `-as [type]` guard would
# skip recompiling and the retry would use the stale V2 class (no SendInput /
# message-click methods) -> MethodNotFound at click time. A fresh class name
# forces a recompile on every retry.
if (-not ('WatchOfflineVisionV3' -as [type])) {
# Single-quoted here-string: the C# body is passed to Add-Type verbatim.
# A double-quoted (interpolating) here-string would let PowerShell evaluate
# $($_.Exception.Message) at script top-level, where $_ is undefined ->
# VariableIsUndefined RuntimeException before ANY dialog logic runs (the
# 2026-08-05 OD-049 launch blocker; introduced by the PSSA triage adding
# that Write-Verbose into the C# catch).
#
# Reference resolution (2026-08-06 FRESH15 launch blocker): on .NET
# Framework (Windows PowerShell 5.1) the drawing types live in System.Drawing
# (GAC); on .NET Core/5+ (pwsh 7) they were forwarded to System.Drawing.Common
# and the old `-ReferencedAssemblies System.Drawing.dll` could not resolve ->
# CS1069 'Bitmap' forwarded to 'System.Drawing.Common' at compile time, killing
# the watch step (watch_exit=4) before any dialog logic ran. Resolve the
# ACTUAL assembly location on the running engine and reference that; each
# Add-Type -AssemblyName is best-effort (the name the engine lacks just fails
# silently). Keep the guard above so a second invocation never recompiles.
try { Add-Type -AssemblyName System.Drawing -ErrorAction Stop } catch { }
try { Add-Type -AssemblyName System.Drawing.Common -ErrorAction Stop } catch { }
# try/catch (NOT -ErrorAction SilentlyContinue): the probe and the launcher
# both run $ErrorActionPreference='Stop', which promotes the missing-assembly
# error to terminating.
$sdCandidates = @()
try { $sdCandidates += [System.Drawing.Bitmap].Assembly.Location } catch { }
# .NET 10 split GDI+ internals into System.Private.Windows.GdiPlus and
# Add-Type's default reference set is incomplete on pwsh 7.6: Thread (CS0234)
# and IGraphics (CS0012) both needed explicit refs during the FRESH15 fix.
# Belt-and-braces: load GdiPlus by name AND reference the whole shared-
# framework runtime directory, so no further transitive dep can surface on
# any engine.
try {
    $gdiPlus = [System.Reflection.Assembly]::Load('System.Private.Windows.GdiPlus')
    $sdCandidates += $gdiPlus.Location
} catch { }
try {
    $runtimeDir = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    foreach ($dll in (Get-ChildItem -LiteralPath $runtimeDir -Filter '*.dll' -File -ErrorAction Stop)) {
        $sdCandidates += $dll.FullName
    }
} catch { }
# Dedupe by SIMPLE NAME, not path: the explicit drawing location and the
# runtime dir can both carry System.Drawing (same identity, different path),
# and the .NET Framework compiler rejects duplicate identities ('An assembly
# with the same identity ... has already been imported'). GetAssemblyName
# also skips native images (CS0009) - one pass does both jobs.
$sdRefs = @()
$sdSeen = @{}
foreach ($path in $sdCandidates) {
    try { $sdName = [System.Reflection.AssemblyName]::GetAssemblyName($path).Name } catch { continue }
    if ($sdSeen.ContainsKey($sdName)) { continue }
    $sdSeen[$sdName] = $true
    $sdRefs += $path
}
Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WatchOfflineVisionV3 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  // Sleep via kernel32, NOT System.Threading.Thread.Sleep: pwsh 7.6's Add-Type
  // cannot resolve System.Threading.Thread even when its assembly location is
  // passed in -ReferencedAssemblies (CS0234; verified with a minimal repro),
  // and kernel32 is referenced on every engine.
  [DllImport("kernel32.dll")] public static extern void Sleep(uint ms);
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
    // No ShowWindow here -- SW_RESTORE during LoginOnReplay has correlated with
    // OnBackground / window_lost in live OD pulses.
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
    // NOTE: keep this catch EMPTY C# - never reference PowerShell ($_ /
    // Write-Verbose) here. The Add-Type here-string is single-quoted (no
    // PS interpolation), so any PS syntax lands in the C# compiler.
    try { SetProcessDPIAware(); } catch { }
    _dpiAware = true;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }
  public const uint INPUT_MOUSE = 0;
  public const uint WM_LBUTTONDOWN = 0x0201;
  public const uint WM_LBUTTONUP = 0x0202;
  public const int MK_LBUTTON = 0x0001;
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

  // Legacy mouse_event click (kept for back-compat; the script uses the
  // SendInput / message channels below).
  public static void ClickScreen(int x, int y) {
    EnsureDpiAware();
    SetCursorPos(x, y);
    Sleep(100);
    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    Sleep(70);
    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
  }

  // Reliable real-input click: hover, press (down), hold, release. Uses
  // SendInput (the same injection the OS gives physical input), NOT the legacy
  // mouse_event - DAVA/Unity-style UI can swallow synthesized mouse_event
  // clicks, and the old blind double-click (two clicks 250ms apart with no
  // verification) could double-trigger or hit a moving button. Returns whether
  // BOTH the down and up events were accepted by the input system.
  public static bool ClickScreenSendInput(int x, int y, int hoverMs, int holdMs) {
    EnsureDpiAware();
    SetCursorPos(x, y);
    Sleep((uint)hoverMs);
    var down = new INPUT[1];
    down[0].type = INPUT_MOUSE;
    down[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
    uint rd = SendInput(1, down, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    Sleep((uint)holdMs);
    var up = new INPUT[1];
    up[0].type = INPUT_MOUSE;
    up[0].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    uint ru = SendInput(1, up, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    return rd == 1 && ru == 1;
  }

  // Message-based click at CLIENT coordinates: delivers WM_LBUTTONDOWN/UP
  // directly to the game window's message queue via PostMessage. Works even
  // when a covering window holds the foreground (the SendInput channel is
  // swallowed by the foreground lock), because the message goes straight to
  // the target hWnd. Some engines ignore posted mouse messages - hence the
  // alternating-channel design - but Blitz's DAVA widgets accept them.
  public static void ClickClientMessage(IntPtr hWnd, int clientX, int clientY) {
    EnsureDpiAware();
    IntPtr lp = (IntPtr)(((clientY & 0xFFFF) << 16) | (clientX & 0xFFFF));
    PostMessage(hWnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
    Sleep(70);
    PostMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lp);
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
      // PrintWindow tracks live DAVA frames; CopyFromScreen froze on a stale
      // composited frame (orangePx stuck) during OD pulses.
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
'@ -ReferencedAssemblies $sdRefs
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
    $rect = New-Object WatchOfflineVisionV3+RECT
    $bmp = [WatchOfflineVisionV3]::CaptureBitmap($Hwnd, [ref]$rect)
    if (-not $bmp) { return $null }
    try {
        $dialog = [WatchOfflineVisionV3]::AnalyzeDialog($bmp)
        if ($SavePath) { [void][WatchOfflineVisionV3]::SaveBitmap($bmp, $SavePath) }
        return [pscustomobject]@{
            Rect               = $rect
            Blob               = $dialog.Blob
            DialogMeanLuminance = [double]$dialog.DialogMeanLuminance
            Width              = $bmp.Width
            Height             = $bmp.Height
            # FRESH17: area ratio vs the reference resolution the absolute
            # pixel thresholds were tuned at. Floored so a tiny window cannot
            # collapse a threshold to 0 (a 5px floor keeps the gates sane).
            PxScale            = [Math]::Max(0.05, ($bmp.Width * $bmp.Height) / ([double]$ReferenceWidth * $ReferenceHeight))
        }
    }
    finally {
        $bmp.Dispose()
    }
}

# FRESH17: scale an absolute-pixel threshold by the window-area ratio, floored
# at 5px (a threshold of 0 would make every frame look like the dialog/ready
# gate). Luminance means are NOT scaled - callers only pass pixel counts.
function Get-ScaledThreshold([int]$Base, [double]$Scale) {
    return [Math]::Max(5, [int]($Base * $Scale))
}

function Test-DialogPresent([int]$OrangePx, [double]$DialogMeanL, [double]$PxScale = 1.0) {
    # First dialog sighting: orange blob (incl. dim sync ~63 px at 1080p) or dim
    # modal luminance. Pixel count scaled by window area (FRESH17); luminance is
    # a mean and stays absolute.
    if ($OrangePx -ge (Get-ScaledThreshold 50 $PxScale)) { return $true }
    if ($DialogMeanL -lt $SyncMaxLuminance -and $DialogMeanL -gt 15) { return $true }
    return $false
}

function Test-ReadySample(
    [int]$OrangePx,
    [double]$DialogMeanL,
    [bool]$SeenSyncing,
    [Nullable[datetime]]$FirstBrightAt,
    [Nullable[datetime]]$FirstDialogAt,
    [double]$PxScale = 1.0
) {
    # FRESH17: ready threshold is a pixel count - scale by window area so the
    # gate fires at 640x360 (1/9 the pixels of 1080p).
    if ($OrangePx -lt (Get-ScaledThreshold $ReadyMinOrange $PxScale)) { return $false }
    if ($DialogMeanL -lt $ReadyMinLuminance) { return $false }
    # Prefer post-sync: first bright after dim is the interactive window.
    if ($SeenSyncing) { return $true }
    # Grace: bright idle without observed sync aEUR" must clear both age floors so
    # we do not click at ~2aEUR"3s (dialog dismisses, no Start replay in blitz-log).
    if (-not $FirstBrightAt -or -not $FirstDialogAt) { return $false }
    $brightAge = ((Get-Date) - $FirstBrightAt).TotalSeconds
    $dialogAge = ((Get-Date) - $FirstDialogAt).TotalSeconds
    if ($brightAge -ge $SyncGraceSeconds -and $dialogAge -ge $MinDialogAgeSeconds) {
        return $true
    }
    return $false
}

function Get-CurrentBlitzLog([datetime]$ProcessStartAt) {
    # The current session's DAVA log is the newest blitz-logs_*.txt written
    # at/after the game process started. Filtering on process start time
    # prevents a stale log from a prior session satisfying the replay-start
    # marker (the previous pin-once logic could grab the prior session's file
    # when the script ran before the game created its log, suppressing round-2
    # clicks with stale evidence). The 15s tolerance covers clock granularity;
    # the newest-first sort then guarantees the current session's log wins as
    # soon as it exists.
    $davaDir = Join-Path $env:LOCALAPPDATA 'wotblitz\DAVAProject'
    return Get-ChildItem -LiteralPath $davaDir -Filter 'blitz-logs_*.txt' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $ProcessStartAt.AddSeconds(-15) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Test-ReplayStartedMarker([datetime]$ProcessStartAt) {
    $log = Get-CurrentBlitzLog -ProcessStartAt $ProcessStartAt
    if (-not $log) { return $false }
    return [bool](Select-String -LiteralPath $log.FullName -Pattern 'START_REPLAY_LOCAL|Start replay event' -Quiet)
}

try {
    [void][WatchOfflineVisionV3]::EnsureDpiAware()
    if (-not $VisualDismissOnly) {
        try {
            $null = Get-Rendezvous
        }
        catch {
            Write-Host 'watch_offline: rendezvous_missing'
            Quit-WatchOffline 2
        }
    }
    else {
        Write-Host 'watch_offline: visual_dismiss_only'
    }

    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'watch_offline: no_game_window'
        Quit-WatchOffline 1
    }

    # The replay-start marker is the fastest ground truth that playback began:
    # the game writes 'Start replay event' at dialog dismissal, seconds before
    # the Host lifecycle gate flips. The log is re-resolved per marker check
    # against the game process start time (see Test-ReplayStartedMarker), so a
    # stale log from a prior session can never satisfy the marker.
    $gameProcessStartAt = $game.StartTime

    $before = 'Unknown'
    $beforeReason = ''
    if (-not $VisualDismissOnly) {
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
    }
    else {
        Write-Host ("watch_offline: before=visual_only pid=" + $game.Id)
    }

    # Soft focus only: SW_RESTORE during LoginOnReplay correlated with
    # become hidden -> OnBackground in live OD pulses (see ForceForeground note).
    [void][WatchOfflineVisionV3]::SetForegroundWindow($game.MainWindowHandle)
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
        # Soft-focus only, throttled in ALL phases. The previous logic fired
        # ShowWindow(SW_RESTORE)+SetForegroundWindow on every ~150ms sample
        # once the dialog was sighted (phase != LookingForDialog); SW_RESTORE
        # during LoginOnReplay correlated with become hidden / OnBackground.
        if (((Get-Date) - $lastFocusAt).TotalSeconds -ge 3) {
            [void][WatchOfflineVisionV3]::SetForegroundWindow($game.MainWindowHandle)
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
            if (Test-DialogPresent -OrangePx $orangePx -DialogMeanL $dialogMeanL -PxScale $analysis.PxScale) {
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

            # Owner sync: dim dialog (~31 L) with collapsed-but-nonzero orange (~60aEUR"80
            # at 1080p; scaled by window area). Blank frames (La%^0) and bright
            # low-orange splash must NOT arm SeenSyncing.
            $syncOrangeFloor = Get-ScaledThreshold 30 $analysis.PxScale
            $syncOrangeCeil = Get-ScaledThreshold $SyncMaxOrange $analysis.PxScale
            $looksSyncing = (
                $dialogMeanL -gt 18 -and
                $dialogMeanL -lt $SyncMaxLuminance -and
                $orangePx -ge $syncOrangeFloor -and
                $orangePx -lt $syncOrangeCeil
            )
            if ($looksSyncing) {
                $seenSyncing = $true
                $firstBrightAt = $null
                Write-Host ("watch_offline: syncing_observed dialogMeanL={0} orangePx={1}" -f $dialogMeanL, $orangePx)
            }
            elseif ($orangePx -ge (Get-ScaledThreshold $ReadyMinOrange $analysis.PxScale) -and $dialogMeanL -ge $ReadyMinLuminance) {
                if (-not $firstBrightAt) {
                    $firstBrightAt = Get-Date
                    Write-Host 'watch_offline: first_bright_ready_looking'
                }
            }

            # After sync clears, click on the first bright+strong frame.
            $needStable = if ($seenSyncing) { 1 } else { $StableSamples }
            if (Test-ReadySample -OrangePx $orangePx -DialogMeanL $dialogMeanL -SeenSyncing $seenSyncing -FirstBrightAt $firstBrightAt -FirstDialogAt $firstDialogAt -PxScale $analysis.PxScale) {
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
    # FRESH16: entrance-animation settle. The ready gate accepted the FIRST
    # bright frame (post-sync StableSamples=1), but the CTA is still animating
    # in at that instant - a click lands before the widget is hit-testable and
    # the game drops it (the user-observed 'click does not register'). Wait
    # until the blob CENTROID stops moving across consecutive samples (or a
    # cap), so the click happens on a fully-rendered button. Centroid stability
    # beats pixel-count stability: the count stays ~flat during a fade, but the
    # button's position/shape moving is what marks the animation in progress.
    $settleSamplesOk = 0
    $prevCentroidX = -1
    $prevCentroidY = -1
    $settleDeadline = (Get-Date).AddSeconds([Math]::Max(1, ($SettleSamples * $SettleSampleIntervalMs / 1000.0) * 4))
    while ((Get-Date) -lt $settleDeadline -and $settleSamplesOk -lt $SettleSamples) {
        Start-Sleep -Milliseconds $SettleSampleIntervalMs
        $settleGame = Get-GameWindow
        if (-not $settleGame) {
            Write-Host 'watch_offline: window_lost_during_settle'
            Quit-WatchOffline 1
        }
        $settleAnalysis = Get-WindowAnalysis $settleGame.MainWindowHandle $ScreenshotPath
        if (-not $settleAnalysis -or -not $settleAnalysis.Blob.Found) {
            $settleSamplesOk = 0
            $prevCentroidX = -1
            $prevCentroidY = -1
            continue
        }
        $cxNow = [int]$settleAnalysis.Blob.CentroidX
        $cyNow = [int]$settleAnalysis.Blob.CentroidY
        if ($prevCentroidX -lt 0) {
            $prevCentroidX = $cxNow
            $prevCentroidY = $cyNow
            continue
        }
        $moved = [Math]::Abs($cxNow - $prevCentroidX) + [Math]::Abs($cyNow - $prevCentroidY)
        $prevCentroidX = $cxNow
        $prevCentroidY = $cyNow
        if ($moved -le $SettleMaxCentroidDeltaPx) {
            $settleSamplesOk++
        }
        else {
            $settleSamplesOk = 0
        }
    }
    Write-Host ("watch_offline: animation_settle samples={0}/{1} centroid={2},{3}" -f `
        $settleSamplesOk, $SettleSamples, $prevCentroidX, $prevCentroidY)
    # Brief settle after sync recovery so the CTA accepts input; still short vs Error 126.
    $holdMs = if ($seenSyncing) { 350 } else { [int]($ReadyHoldSeconds * 1000) }
    Write-Host ("watch_offline: phase=Ready dialogMeanL={0} orangePx={1} seenSync={2} stable={3} hold_{4}ms" -f `
        [Math]::Round([double]$readyAnalysis.DialogMeanLuminance, 1), $preCount, $seenSyncing, $stable, $holdMs)
    if ($holdMs -gt 0) {
        Start-Sleep -Milliseconds $holdMs
    }

    # Re-check after hold aEUR" dialog may have timed out during hold.
    $game = Get-GameWindow
    if (-not $game) {
        Write-Host 'watch_offline: window_lost_after_hold'
        Quit-WatchOffline 1
    }
    [WatchOfflineVisionV3]::ForceForeground($game.MainWindowHandle)
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

    $minBlobScaled = Get-ScaledThreshold $MinBlobPixels $analysis.PxScale
    if ($preCount -lt $minBlobScaled -and $before -ne 'OfflineReplayVerified') {
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
    $sawBlob = $preCount -ge $minBlobScaled
    $gateOk = $false
    $dialogGone = $false

    for ($round = 1; $round -le $MaxRounds; $round++) {
        if ((Get-Date) -ge $deadline) { break }

        $game = Get-GameWindow
        if (-not $game) {
            Write-Host 'watch_offline: window_lost'
            Quit-WatchOffline 1
        }

        # Soft focus only (no ShowWindow/SW_RESTORE churn); ForceForeground
        # before the click handles focus without SW_RESTORE.
        [void][WatchOfflineVisionV3]::SetForegroundWindow($game.MainWindowHandle)
        Start-Sleep -Milliseconds 250

        $analysis = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
        if (-not $analysis) {
            Write-Host 'watch_offline: capture_failed_round'
            continue
        }

        $count = [int]$analysis.Blob.PixelCount
        Write-Host ("watch_offline: round={0} orange_pixels={1} found={2}" -f $round, $count, $analysis.Blob.Found)

        # Blitz-log ground truth beats the Host gate: the game writes
        # 'Start replay event' when playback begins, seconds before the
        # lifecycle monitor flips OfflineReplayVerified. In OD-044 the gate
        # took ~9-10s, outliving the 8s poll window, so round 2 re-clicked the
        # live replay HUD (OD-017 false positive) and the focus churn hid the
        # window (become hidden -> OnBackground).
        #
        # Round 1 still clicks whenever the dialog blob is visibly present,
        # EVEN IF the marker already fired (the game can auto-start playback
        # ~8s after the dialog appears without dismissing it; leaving the
        # dialog up over the live replay makes the game tear down early - seen
        # in the 2026-08-04 validation runs). The marker only forbids clicks
        # in rounds >= 2, once round 1 has had its chance to dismiss it.
        $replayStarted = $false
        if ($round -gt 1 -and (Test-ReplayStartedMarker -ProcessStartAt $gameProcessStartAt)) {
            $replayStarted = $true
            Write-Host 'watch_offline: replay_started_marker (no further clicks)'
        }

        if (-not $replayStarted -and $count -ge (Get-ScaledThreshold $MinBlobPixels $analysis.PxScale)) {
            $rx = $analysis.Blob.CentroidX / [double][Math]::Max(1, $analysis.Width)
            $ry = $analysis.Blob.CentroidY / [double][Math]::Max(1, $analysis.Height)
            if ($rx -lt 0.18 -or $rx -gt 0.48 -or $ry -lt 0.38 -or $ry -gt 0.62) {
                Write-Host ("watch_offline: skip_click_outside_dialog_band cxRatio={0:N2} cyRatio={1:N2}" -f $rx, $ry)
            }
            else {
                $sawBlob = $true
                $screenX = $analysis.Rect.Left + $analysis.Blob.CentroidX
                $screenY = $analysis.Rect.Top + $analysis.Blob.CentroidY
                # FRESH16: ONE click per round, alternating channels - never the
                # old blind double-click (two clicks 250ms apart, no verification
                # between: the second could double-trigger a button still mid-
                # entrance-animation, or miss as the button moves). SendInput on
                # odd rounds is the primary real-input channel; even rounds use
                # the PostMessage client-message channel, which reaches the game
                # window even when a covering window has stolen the foreground.
                $useMessageClick = ($MessageClickEveryRound -gt 0 -and ($round % $MessageClickEveryRound) -eq 0)
                if ($useMessageClick) {
                    Write-Host ("watch_offline: click_message_channel screen={0},{1} client={2},{3}" -f `
                        $screenX, $screenY, $analysis.Blob.CentroidX, $analysis.Blob.CentroidY)
                    [WatchOfflineVisionV3]::ClickClientMessage($game.MainWindowHandle, $analysis.Blob.CentroidX, $analysis.Blob.CentroidY)
                }
                else {
                    Write-Host ("watch_offline: click_sendinput screen={0},{1}" -f $screenX, $screenY)
                    [WatchOfflineVisionV3]::ForceForeground($game.MainWindowHandle)
                    Start-Sleep -Milliseconds 100
                    $clickOk = [WatchOfflineVisionV3]::ClickScreenSendInput($screenX, $screenY, $ClickHoverMs, $ClickHoldMs)
                    Write-Host ("watch_offline: sendinput_accept=" + $clickOk)
                }
                # Verify the click landed BEFORE the next round: a quick
                # re-capture right after the click catches an immediately
                # dismissed dialog (button gone) so round 2 does not re-click a
                # live replay HUD (OD-017 false positive).
                Start-Sleep -Milliseconds 400
                $quickPost = Get-WindowAnalysis $game.MainWindowHandle $null
                if ($quickPost -and $quickPost.Blob.Found -and [int]$quickPost.Blob.PixelCount -gt (Get-ScaledThreshold $DismissMaxPixels $quickPost.PxScale)) {
                    Write-Host ("watch_offline: post_click_blob_still_present px=" + [int]$quickPost.Blob.PixelCount)
                }
            }
        }
        elseif (-not $sawBlob -and $round -eq 1) {
            Write-Host 'watch_offline: no_blob_this_round'
        }

        $pollUntil = (Get-Date).AddSeconds(8)
        if ($pollUntil -gt $deadline) { $pollUntil = $deadline }
        do {
            Start-Sleep -Seconds 1
            # Fast evidence in BOTH modes: the replay-start marker stops any
            # further clicks once round 1 has had its chance to dismiss the
            # dialog. In visual-only mode it is the success gate; in Host mode
            # the gate is awaited separately so the launcher still observes
            # OfflineReplayVerified.
            if ($round -gt 1 -and -not $replayStarted -and
                (Test-ReplayStartedMarker -ProcessStartAt $gameProcessStartAt)) {
                $replayStarted = $true
                Write-Host 'watch_offline: poll=replay_started_marker (no further clicks)'
            }
            if ($VisualDismissOnly) {
                if ($replayStarted) { $gateOk = $true; break }
                Write-Host 'watch_offline: poll=visual_only'
            }
            else {
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
            }
        } while ((Get-Date) -lt $pollUntil)

        Start-Sleep -Milliseconds 500
        $post = Get-WindowAnalysis $game.MainWindowHandle $ScreenshotPath
        if ($post) {
            $postCount = [int]$post.Blob.PixelCount
            Write-Host ("watch_offline: post_orange_pixels={0}" -f $postCount)
            $dismissScaled = Get-ScaledThreshold $DismissMaxPixels $post.PxScale
            if ($postCount -le $dismissScaled) { $dialogGone = $true }
            elseif ($sawBlob -and $preCount -gt 0 -and $postCount -lt [Math]::Max($dismissScaled, [int]($preCount * 0.15))) {
                $dialogGone = $true
                Write-Host 'watch_offline: dialog_shrunk_ok'
            }
        }

        Write-Host ("watch_offline: gateOk={0} dialogGone={1} replayStarted={2}" -f $gateOk, $dialogGone, $replayStarted)
        # OfflineReplayVerified proves the replay started (lifecycle monitor
        # requires a fresh START_REPLAY_LOCAL marker), which proves the dialog
        # is gone. The orange dialog ROI then false-positives on replay-HUD
        # content (OD-017), so trust the marker/gate over the blob.
        # Non-visual: the round loop continues (without clicking) until the
        # Host gate flips, preserving the launcher's post-check contract.
        if ($gateOk -and ($dialogGone -or -not $VisualDismissOnly)) { break }
        if ($replayStarted -and $VisualDismissOnly) { $gateOk = $true; break }
    }

    $after = if ($VisualDismissOnly) { 'visual_only' } else { Get-VerificationState }
    $finalGame = Get-GameWindow
    if (-not $finalGame) {
        Write-Host 'watch_offline: window_lost_final'
        Quit-WatchOffline 1
    }
    $final = Get-WindowAnalysis $finalGame.MainWindowHandle $ScreenshotPath
    $finalCount = if ($final) { [int]$final.Blob.PixelCount } else { -1 }
    Write-Host ("watch_offline: after={0} final_orange_pixels={1}" -f $after, $finalCount)

    $finalScale = if ($final) { $final.PxScale } else { 1.0 }
    $dismissScaledFinal = Get-ScaledThreshold $DismissMaxPixels $finalScale
    $dialogGone = $dialogGone -or ($finalCount -ge 0 -and $finalCount -le $dismissScaledFinal)
    if (-not $VisualDismissOnly) {
        $gateOk = $gateOk -or ($after -eq 'OfflineReplayVerified')
        # Verified gate proves the replay started, which proves the dialog is
        # gone; the orange ROI may show replay-HUD content instead (OD-017).
        if ($after -eq 'OfflineReplayVerified') { $dialogGone = $true }
    }
    else {
        $markerOk = Test-ReplayStartedMarker -ProcessStartAt $gameProcessStartAt
        if ($markerOk) {
            # Playback began (blitz-log ground truth): the replay-HUD orange
            # false-positive (OD-017) makes the final-frame pixel check
            # unreliable once playback is live, so the marker resolves the
            # dialog check in visual-only too. Note the marker proves playback
            # STARTED, not that a dialog is gone -- dismissal is round 1's
            # click's job; in the hangar flow there is no dialog at all.
            $gateOk = $true
            $dialogGone = $true
        }
        else {
            $dialogGone = ($finalCount -ge 0 -and $finalCount -le $dismissScaledFinal)
            if ($dialogGone) { $gateOk = $true }
        }
    }

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
    catch { Write-Verbose "watch_offline: blitz-log marker probe failed (log mid-rotation): $($_.Exception.Message)" }

    Write-Host ("watch_offline: FAILED gateOk={0} dialogGone={1}" -f $gateOk, $dialogGone)
    Quit-WatchOffline 3
}
catch {
    if ($_.Exception.Message -match '^WATCH_EXIT:') { throw }
    Write-Host ("watch_offline: error=" + $_.Exception.Message)
    Quit-WatchOffline 4
}


