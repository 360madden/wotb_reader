#Requires -Version 5.1
<#
.SYNOPSIS
  Pre-staged camera-state verification session (CAM-001). Validates the
  static camera ownership chain (handoff
  2026-08-11-camera-ownership-root.md) against one positively verified
  offline replay launch, and correlates the live cameraState object against
  the decoded replay ground truth.

.DESCRIPTION
  Static discovery pinned the W2S camera anchor as a fixed member-path:

      avatar (vftable RVA 0x3277e8c replay / 0x3277da4 live)
        [avatar+0x154] -> BattleResources
        [br+0x2C]      -> camera (ReplayCameraController RVA 0x326dd0c /
                                   CameraController RVA 0x32de028 live)
        [cam+0x28]     -> cameraState (GameCamera, RVA 0x32dafa0)

  v5 (live-verified 2026-08-11): the POSE lives on the GameCamera, NOT the
  ReplayCameraController (which is a frozen shell — no live fields). The
  GameCamera pose layout (diff-scan verified):

      position        +0x38/+0x3C/+0x40   (interpolated prev copy +0x44..)
      yaw cos/sin     +0x50/+0x54         (yaw = atan2(sin, cos))
      pitch           +0x58
      view basis      +0x80..0xB0
      extra pos copy  +0xB0/+0xB4/+0xB8

  Correlation is now done in MEMORY space: the viewpoint tank position is
  read at the same wall time as the camera, so the camera-to-tank offset
  needs no decoded-clock alignment. The yaw is additionally aligned to the
  decoded frame yaw over the timeline (same convention confirmed: 0.0027
  rad best match) to bridge into decoded space for the W2S path.

  CORRECTED v6 (2026-08-11): /discover/entity-position (and
  /discover/position-page) are gated on the resolver's hard-coded vtable
  checks (session-controller-vtable etc.) which only match ONE game phase
  — today's sessions hit a different session-controller vftable
  (base+0x325ad2c vs the recorded 0x323d9bc), so both endpoints refuse. The
  od-073 24/24 evidence (2026-08-09) proves the underlying chain resolves
  during replay playback, so v6 falls back to a DIRECT manual walk of the
  resolver chain (gameCore -> app -> session -> account -> playback ->
  connection -> entities -> cached/tree -> entity -> movementFilter ->
  avatar-helper ring -> position record) with NO vtable gates, using the
  same layout constants. The reads happen in the od-073-proven window
  (StageDelaySeconds default 55 = gate+55s..+72s), and the memory tank
  position is compared against the decoded trajectory at the yaw-aligned
  time to record the memory<->decoded origin delta (coordinate
  calibration).

  ASLR note (verified 2026-08-11 on the hash-pinned binary): wotblitz.exe
  has DllCharacteristics 0x8140 (DYNAMIC_BASE set), so the runtime vftable
  pointer is (runtime module base + RVA), not the preferred-base constant.
  This session therefore learns the runtime module base from the pattern
  scan response's baseAddress, computes (base + 0x3277e8c), and scans for
  that dword - the anchor scan is base-relative, not a fixed constant.

  This session:
    1. Waits for the OfflineReplayVerified gate (bounded).
    2. Binds the launch artifact to its newest decoded session and picks the
       single viewpoint entity (same binding as the od-073 family).
    3. Learns the runtime module base from a pattern scan, then scans the
       verified process for the avatar vftable dword (base + 0x3277e8c).
    4. Walks [avatar+0x154] -> [br+0x2C] -> [cam+0x28] via the server-owned
       read endpoint, with identity gates on the camera vftables
       (ReplayCameraController base+0x326dd0c, GameCamera base+0x32dafa0 -
       CAM-002 verified), then reads the GameCamera pose fields.
    5. Correlates:
       a. camera yaw atan2(sin@+0x54, cos@+0x50) vs the decoded frame
          camera yaw (same convention proven to 0.15 deg, CAM-002).
       b. camera posA +0x38 vs the viewpoint tank position at the same
          wall time (memory space; /discover/entity-position or the
          CAM-008 pre-login-aware direct walk) - the delta norm is the
          third-person offset (verified 23.57 m, CAM-004).
       c. finite-value + view-basis sanity checks on the +0x80..0xB0 rows.
    6. Writes a fresh CAM-001 aggregate (schema
       wotbtreader.cam001.camera-state-verify.v7) including per-round
       pose + decoded-tank samples for verify-camera-projection.py.

  CORRECTED 2026-08-11 (first live run): the chain walk reads at
  (current + displacement) and advances to the pointer VALUE — the v1 walk
  added the displacement to the pointer value, dereferencing .rdata text
  instead of the object chain (the first run's 3/3 was garbage). v2 also
  records yawDeltaRadians / thirdPersonOffsetNorm whenever the comparison
  runs (not only when in range), keeping the aggregate diagnosable.

  CORRECTED v3 (same session, branch on evidence): the POSE fields live on
  the camera CONTROLLER object (the chain's hop 2), not on the GameCamera
  ring object at [cam+0x28]. The per-frame dispatcher FUN_01ddb130
  integrates position at param_1[0x47..0x49] = [controller+0x11C..0x124]
  and updates +0xAC..0xB4 (param_1[0x2b..0x2d]) — param_1 is the
  polymorphic camera controller (virtual calls through *param_1). Live
  identity probe confirmed the walked hop-2 object's vftable is
  ReplayCameraController (runtime base+0x326dd0c). v3 therefore reads the
  pose fields from the camera controller and verifies its vftable as an
  integrity gate; the GameCamera ring ([cam+0x28], class GameCamera) is
  read for ring liveness only.

  CORRECTED v3.1: position correlation is now nearest-in-time (min norm
  over the subsampled trajectory) instead of a fixed t=30 sample, so the
  load-time skew between the memory clock and the decoded clock cannot
  manufacture a miss. The recorded norm is the min over all samples; the
  matched sample index/seconds are also recorded.

  CORRECTED v5 (same session, decisive branch): v3 read the pose from the
  camera controller — WRONG, that object is frozen. Live diff-scan showed
  all pose fields on the GameCamera (vftable base+0x32dafa0): position at
  +0x38/+0x3C/+0x40 (prev-frame copy +0x44), yaw as cos/sin at
  +0x50/+0x54, pitch +0x58, basis +0x80..0xB0. v5 reads those fields and
  correlates the camera-to-tank offset IN MEMORY SPACE via
  /discover/entity-position (same-wall-time, no decoded-clock alignment
  needed), plus a yaw alignment against the decoded frame yaw timeline.

  Privacy: aggregate correlation statistics + the per-round camera/tank
  WORLD POSITIONS that feed verify-camera-projection.py (flagged
  coordinatesPersisted = true in the privacy block - the W2S validator
  needs them). No entity id, process addresses, raw bytes, paths, or
  capability values are persisted.

.EXITCODES
  0  Campaign completed and a fresh aggregate result was written.
  1  Invalid options, missing rendezvous, or gate not verified.
  2  Ground truth / launch artifact binding unavailable.
  3  Memory walk failed (anchor scan, chain, or field reads).
  4  Aggregate result could not be created.
#>
[CmdletBinding()]
param(
    [string]$SessionId = '',
    [int]$WaitVerifiedSeconds = 180,
    [int]$StageDelaySeconds = 55,
    [int]$ReadCount = 6,
    [int]$ReadIntervalMilliseconds = 750,
    [int]$MaxCandidates = 20,
    [string]$ResultPath = '',
    # CAM-001 v7 root-cause follow-up (2026-08-11): capture the wotblitz
    # window in-memory each round and persist ONLY derived sky/terrain
    # scalars (never raw pixels) so the offline validator can discriminate
    # a non-chase camera STATE (high above/behind, level pitch) from a
    # wrong-pose read. Off by default; existing runs unchanged.
    [switch]$CaptureWindow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($WaitVerifiedSeconds -lt 1 -or $WaitVerifiedSeconds -gt 300 -or
    $StageDelaySeconds -lt 0 -or $StageDelaySeconds -gt 180 -or
    $ReadCount -lt 1 -or $ReadCount -gt 60 -or
    $ReadIntervalMilliseconds -lt 100 -or $ReadIntervalMilliseconds -gt 5000 -or
    $MaxCandidates -lt 1 -or $MaxCandidates -gt 200) {
    Write-Host 'cam001: FAILED_invalid_options'
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $ResultPath = Join-Path $repoRoot ('.data\cam001-camera-state-verify-' + $stamp + '.json')
}
if (Test-Path -LiteralPath $ResultPath) {
    Write-Host 'cam001: FAILED_result_exists'
    exit 4
}

function Test-Finite([object]$Value) {
    # PS 5.1-compatible finite check (double.IsFinite is .NET Core 3.0+ only).
    if ($null -eq $Value) { return $false }
    try {
        $d = [double]$Value
    }
    catch {
        return $false
    }
    return -not ([double]::IsNaN($d) -or [double]::IsInfinity($d))
}

# ---- CAM-001 v7 root-cause follow-up: in-memory window capture -------
# The mode-vs-pose discriminator needs to know what the game actually
# renders. Capturing the shrunk wotblitz window and persisting derived
# scalars (sky fraction / horizon row / mean luminance) — NEVER raw
# pixels — lets the offline validator classify the camera state. The C#
# helper mirrors the launcher's PrintWindow capture (click-watch-offline),
# scaled down to just capture + luminance analysis.
function Initialize-CamCaptureHelper {
    if ('CamCaptureV1' -in ([AppDomain]::CurrentDomain.GetAssemblies() |
            ForEach-Object { try { $_.GetType('CamCaptureV1') } catch { $null } } |
            Where-Object { $null -ne $_ })) {
        return
    }
    try { Add-Type -AssemblyName System.Drawing -ErrorAction Stop } catch { }
    try { Add-Type -AssemblyName System.Drawing.Common -ErrorAction Stop } catch { }
    $source = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CamCaptureV1 {
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

  // Returns a 32bpp bitmap of the window, or null when the window is too
  // small / not capturable. Caller must dispose.
  public static Bitmap CaptureWindow(IntPtr hWnd) {
    EnsureDpiAware();
    RECT r;
    if (!GetWindowRect(hWnd, out r)) return null;
    int w = r.Right - r.Left;
    int h = r.Bottom - r.Top;
    if (w < 64 || h < 64) return null;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      bool ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
      g.ReleaseHdc(hdc);
      if (!ok) { g.Dispose(); bmp.Dispose(); return null; }
    }
    return bmp;
  }

  // Derived sky/terrain scalars only. Downsample to a 16x9 luminance grid
  // and return: skyFraction (rows above the horizon line, mean >= sky
  // threshold), horizonRow (the row where the vertical luminance gradient
  // is largest, 0..1 from the top), topMean / bottomMean luminance.
  public static string Analyze(Bitmap bmp) {
    int gw = 16, gh = 9;
    double[] grid = new double[gw * gh];
    using (var small = new Bitmap(bmp, gw, gh)) {
      for (int y = 0; y < gh; y++) {
        for (int x = 0; x < gw; x++) {
          Color c = small.GetPixel(x, y);
          grid[y * gw + x] = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }
      }
    }
    double topMean = 0, bottomMean = 0;
    int half = gh / 2;
    for (int y = 0; y < gh; y++) {
      double rowSum = 0;
      for (int x = 0; x < gw; x++) rowSum += grid[y * gw + x];
      double rowMean = rowSum / gw;
      if (y < half) topMean += rowMean; else bottomMean += rowMean;
    }
    topMean /= half;
    bottomMean /= (gh - half);
    // horizon row = argmax of |rowMean(y+1) - rowMean(y)|
    double bestGrad = -1; double horizonRow = 0.5;
    for (int y = 0; y < gh - 1; y++) {
      double m0 = 0, m1 = 0;
      for (int x = 0; x < gw; x++) { m0 += grid[y * gw + x]; m1 += grid[(y + 1) * gw + x]; }
      m0 /= gw; m1 /= gw;
      double grad = Math.Abs(m1 - m0);
      if (grad > bestGrad) { bestGrad = grad; horizonRow = (y + 0.5) / gh; }
    }
    double skyFraction = 0;
    for (int y = 0; y < gh; y++) {
      double rowMean = 0;
      for (int x = 0; x < gw; x++) rowMean += grid[y * gw + x];
      rowMean /= gw;
      // A level/high camera: bright sky occupies the rows above the
      // horizon; use mean+0.15 as the sky threshold.
      if (rowMean >= (topMean + bottomMean) / 2.0 + 0.15 && rowMean > 0.5) skyFraction++;
    }
    skyFraction /= gh;
    return string.Format("{0:F4}|{1:F4}|{2:F4}|{3:F4}", skyFraction, horizonRow, topMean, bottomMean);
  }
}
'@
    Add-Type -TypeDefinition $source -ReferencedAssemblies @(
        'System.Drawing', 'System.Drawing.Primitives', 'System.Runtime.InteropServices')
}

function Get-CamWindowScalars {
    # Returns a hashtable of derived scalars or $null when the game window
    # is unavailable. NEVER persists raw pixels.
    try {
        Initialize-CamCaptureHelper
        $game = Get-Process -Name wotblitz -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
        if ($null -eq $game) { return $null }
        $bmp = [CamCaptureV1]::CaptureWindow($game.MainWindowHandle)
        if ($null -eq $bmp) { return $null }
        try {
            $parts = ([string][CamCaptureV1]::Analyze($bmp)).Split('|')
            return [ordered]@{
                skyFraction   = [double]$parts[0]
                horizonRow    = [double]$parts[1]
                topMeanLum    = [double]$parts[2]
                bottomMeanLum = [double]$parts[3]
            }
        }
        finally {
            $bmp.Dispose()
        }
    }
    catch {
        return $null
    }
}

function Test-OwnerOnlyRendezvousFile([string]$Path) {
    try {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $file = Get-Item -LiteralPath $Path
        $directory = Get-Item -LiteralPath $file.DirectoryName
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }

        $directoryAcl = Get-Acl -LiteralPath $directory.FullName
        $directoryOwner = (New-Object Security.Principal.NTAccount($directoryAcl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $directoryRules = @($directoryAcl.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        if (-not $directoryAcl.AreAccessRulesProtected -or $directoryOwner -ne $owner -or
            $directoryRules.Count -ne 1 -or
            $directoryRules[0].IdentityReference -ne $owner -or
            $directoryRules[0].AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            (($directoryRules[0].FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl)) {
            return $false
        }

        $fileAcl = Get-Acl -LiteralPath $file.FullName
        $fileOwner = (New-Object Security.Principal.NTAccount($fileAcl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $fileRules = @($fileAcl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]))
        return $fileOwner -eq $owner -and $fileRules.Count -eq 1 -and
            $fileRules[0].IdentityReference -eq $owner -and
            $fileRules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($fileRules[0].FileSystemRights -band
                    [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl)
    }
    catch {
        return $false
    }
}

function Get-Rendezvous {
    try {
        $directory = Join-Path $env:LOCALAPPDATA 'WotBTreader\rendezvous'
        $file = Get-ChildItem -LiteralPath $directory -File -ErrorAction Stop |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -eq $file -or
            -not (Test-OwnerOnlyRendezvousFile -Path $file.FullName)) {
            return $null
        }

        $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (-not $record.PSObject.Properties['baseUri'] -or
            -not $record.PSObject.Properties['capability'] -or
            [string]::IsNullOrWhiteSpace([string]$record.baseUri) -or
            [string]::IsNullOrWhiteSpace([string]$record.capability)) {
            return $null
        }

        $uri = [Uri][string]$record.baseUri
        if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') {
            return $null
        }

        return $record
    }
    catch {
        return $null
    }
}

function Invoke-OdApi {
    param(
        [string]$Method,
        [string]$RelativePath,
        [object]$Body = $null
    )

    $rendezvous = Get-Rendezvous
    if ($null -eq $rendezvous) {
        throw [InvalidOperationException]::new('rendezvous_unavailable')
    }

    $arguments = @{
        Uri        = [string]$rendezvous.baseUri + $RelativePath
        Method     = $Method
        TimeoutSec = 30
        Headers    = @{
            'X-WotBTreader-Capability' = [string]$rendezvous.capability
        }
    }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = $Body | ConvertTo-Json -Depth 6 -Compress
    }

    return Invoke-RestMethod @arguments
}

function Get-LaunchArtifactId {
    try {
        $marker = Join-Path (Join-Path $env:LOCALAPPDATA 'WotBTreader\od-launch') `
            'artifact.id'
        if (-not (Test-Path -LiteralPath $marker) -or
            -not (Test-OwnerOnlyRendezvousFile -Path $marker)) {
            return $null
        }

        $file = Get-Item -LiteralPath $marker
        if ($file.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddMinutes(-20)) {
            return $null
        }

        $value = (Get-Content -LiteralPath $marker -Raw).Trim()
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse($value, [ref]$parsed) -or $parsed -eq [Guid]::Empty) {
            return $null
        }

        return $parsed.ToString('D')
    }
    catch {
        return $null
    }
}

function Get-LittleEndianHex([uint32]$Value) {
    return (([BitConverter]::GetBytes($Value) | ForEach-Object {
        $_.ToString('X2')
    }) -join '')
}

function Convert-HexToBytes([string]$Hex) {
    # PS 5.1-compatible hex decode ([Convert]::FromHexString is .NET Core 3.0+).
    $clean = ([string]$Hex) -replace '-', ''
    if ($clean.Length -lt 2 -or ($clean.Length % 2) -ne 0) {
        return @()
    }
    $count = $clean.Length / 2
    $bytes = New-Object 'byte[]' $count
    for ($i = 0; $i -lt $count; $i++) {
        $bytes[$i] = [Convert]::ToByte($clean.Substring($i * 2, 2), 16)
    }
    return $bytes
}

function Read-MemoryUInt32([long]$Address) {
    if ($Address -lt 0x10000) { return $null }
    try {
        $read = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/read' `
            -Body @{
                addresses = @(('0x' + $Address.ToString('X')))
                valueKind = 'UInt32'
                valueSize = 4
            }
        $item = $read.reads[0]
        if ($null -eq $item -or -not [bool]$item.readOk -or
            [string]::IsNullOrWhiteSpace([string]$item.observedValueHex)) {
            return $null
        }
        $bytes = Convert-HexToBytes -Hex ([string]$item.observedValueHex)
        if ($bytes.Length -ne 4) { return $null }
        return [long][BitConverter]::ToUInt32($bytes, 0)
    }
    catch {
        return $null
    }
}

function Read-MemoryFloat([long]$Address) {
    if ($Address -lt 0x10000) { return [double]::NaN }
    try {
        $read = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/read' `
            -Body @{
                addresses = @(('0x' + $Address.ToString('X')))
                valueKind = 'Float'
                valueSize = 4
            }
        $item = $read.reads[0]
        if ($null -eq $item -or -not [bool]$item.readOk -or
            [string]::IsNullOrWhiteSpace([string]$item.observedValueHex)) {
            return [double]::NaN
        }
        $bytes = Convert-HexToBytes -Hex ([string]$item.observedValueHex)
        if ($bytes.Length -ne 4) { return [double]::NaN }
        return [double][single][BitConverter]::ToSingle($bytes, 0)
    }
    catch {
        return [double]::NaN
    }
}

function Walk-EntityPositionMemory {
    param(
        [long]$ModuleBase,
        [int]$EntityId
    )
    # Manual replication of the Type10EntityPositionResolver chain for
    # 11.19.0.10 WITHOUT the phase-fragile vtable gates. Same layout
    # constants; the od-073 24/24 evidence proves the chain resolves during
    # replay playback. Returns @{x;y;z;source} or $null.
    $gameCore = Read-MemoryUInt32 -Address ($ModuleBase + 0x04095c88)
    if ($null -eq $gameCore) { return $null }
    $app = Read-MemoryUInt32 -Address ($gameCore + 0x0c)
    $session = Read-MemoryUInt32 -Address ($app + 0x124)
    # CAM-008 (2026-08-11, RTTI-verified): until replay playback starts the
    # session slot holds a PreLoginController (0x325ad2c) whose +0x118 is NOT
    # an account controller. Bail cleanly instead of walking garbage.
    $sessionVftable = Read-MemoryUInt32 -Address $session
    if ($null -eq $sessionVftable -or
        $sessionVftable -eq ($ModuleBase + 0x325ad2c)) { return $null }
    $account = Read-MemoryUInt32 -Address ($session + 0x118)
    $playback = Read-MemoryUInt32 -Address ($account + 0x128)
    if ($null -eq $playback -or $playback -lt 0x10000) { return $null }
    $connection = Read-MemoryUInt32 -Address ($playback + 0x120)
    if ($null -eq $connection -or $connection -lt 0x10000) { return $null }
    $entities = $connection + 0x04

    # FindEntity: cached entity first, then the three trees.
    $entity = $null
    $source = ''
    $cached = Read-MemoryUInt32 -Address ($entities + 0x48)
    if ($null -ne $cached -and $cached -ge 0x10000) {
        $cachedId = Read-MemoryUInt32 -Address ($cached + 0x1c)
        if ($null -ne $cachedId -and $cachedId -eq $EntityId) {
            $entity = $cached
            $source = 'cache'
        }
    }
    if ($null -eq $entity) {
        foreach ($treeOff in @(0x1c, 0x40, 0x34)) {
            $sentinel = Read-MemoryUInt32 -Address ($entities + $treeOff)
            if ($null -eq $sentinel -or $sentinel -lt 0x10000) { continue }
            $node = Read-MemoryUInt32 -Address ($sentinel + 0x04)
            $visited = New-Object 'System.Collections.Generic.HashSet[long]'
            $steps = 0
            while ($null -ne $node -and $node -ge 0x10000 -and $steps -lt 1024) {
                $steps++
                if (-not $visited.Add($node)) { break }
                $left = Read-MemoryUInt32 -Address $node
                $right = Read-MemoryUInt32 -Address ($node + 0x08)
                $nilDword = Read-MemoryUInt32 -Address ($node + 0x0c)
                $key = Read-MemoryUInt32 -Address ($node + 0x10)
                $value = Read-MemoryUInt32 -Address ($node + 0x14)
                $nilFlag = if ($null -eq $nilDword) { 0 } else { ($nilDword -band 0xFF) }
                if ($nilFlag -eq 1) { break }
                if ($null -ne $key -and $key -eq $EntityId -and
                    $null -ne $value -and $value -ge 0x10000) {
                    $entity = $value
                    $source = 'tree'
                    break
                }
                $node = if ($EntityId -lt $key) { $left } else { $right }
            }
            if ($null -ne $entity) { break }
        }
    }
    if ($null -eq $entity) { return $null }

    $movementFilter = Read-MemoryUInt32 -Address ($entity + 0x38)
    if ($null -eq $movementFilter -or $movementFilter -lt 0x10000) { return $null }
    $helper = Read-MemoryUInt32 -Address ($movementFilter + 0x08)
    if ($null -eq $helper -or $helper -lt 0x10000) { return $null }
    $index = Read-MemoryUInt32 -Address ($helper + 0x1c8)
    if ($null -eq $index -or $index -ge 8) { return $null }
    $recordAddress = $helper + 0x08 + $index * 0x38

    # Double-read stability (ring index stable + record unchanged).
    $idxA = Read-MemoryUInt32 -Address ($helper + 0x1c8)
    $x1 = Read-MemoryFloat -Address ($recordAddress + 0x10)
    $y1 = Read-MemoryFloat -Address ($recordAddress + 0x14)
    $z1 = Read-MemoryFloat -Address ($recordAddress + 0x18)
    $idxB = Read-MemoryUInt32 -Address ($helper + 0x1c8)
    $x2 = Read-MemoryFloat -Address ($recordAddress + 0x10)
    $y2 = Read-MemoryFloat -Address ($recordAddress + 0x14)
    $z2 = Read-MemoryFloat -Address ($recordAddress + 0x18)
    if ($null -eq $idxA -or $null -eq $idxB -or
        $idxA -ne $index -or $idxB -ne $index) {
        return $null
    }
    if (-not (Test-Finite -Value $x1) -or -not (Test-Finite -Value $y1) -or
        -not (Test-Finite -Value $z1)) {
        return $null
    }
    if ([Math]::Abs($x1 - $x2) -gt 0.0001 -or
        [Math]::Abs($y1 - $y2) -gt 0.0001 -or
        [Math]::Abs($z1 - $z2) -gt 0.0001) {
        return $null
    }
    return @{ x = $x1; y = $y1; z = $z1; source = $source }
}

function Write-AggregateResult([hashtable]$Aggregate) {
    try {
        $parent = Split-Path -Parent $ResultPath
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }

        $json = $Aggregate | ConvertTo-Json -Depth 8
        $encoding = New-Object Text.UTF8Encoding($false)
        $stream = [IO.File]::Open(
            $ResultPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $writer = New-Object IO.StreamWriter -ArgumentList $stream, $encoding
            try {
                $writer.Write($json)
                $writer.Flush()
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        Write-Host 'cam001: FAILED_result_write'
        exit 4
    }
}

Write-Host 'cam001: waiting_for_verified_gate'
$deadline = [DateTime]::UtcNow.AddSeconds($WaitVerifiedSeconds)
$verified = $false
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $state = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/game/state'
        if ($state.verificationState -eq 'OfflineReplayVerified' -and
            $state.reasonCode -eq 'session.offline_replay_verified') {
            $verified = $true
            $gateObservedAtUtc = [DateTime]::UtcNow
            break
        }
    }
    catch { }
    Start-Sleep -Seconds 1
}
if (-not $verified) {
    Write-Host 'cam001: FAILED_gate_not_verified'
    exit 1
}
Write-Host 'cam001: gate=OfflineReplayVerified'

# ---- Ground truth binding (same as od-073) -------------------------------
$viewpoint = $null
$groundTruthSelection = ''
$battleSessionId = ''
try {
    $launchArtifactId = Get-LaunchArtifactId
    if ([string]::IsNullOrWhiteSpace($launchArtifactId)) {
        Write-Host 'cam001: FAILED_launch_artifact_binding'
        exit 2
    }

    $battleSessionId = $SessionId
    $groundTruthSelection = 'explicit-session'
    if ([string]::IsNullOrWhiteSpace($battleSessionId)) {
        $page = Invoke-OdApi -Method 'Get' -RelativePath '/api/v1/sessions?limit=200'
        $artifactSessions = @($page.items | Where-Object {
            $null -ne $_.session -and
            [string]$_.decodeRun.sourceArtifactId -eq $launchArtifactId
        })
        if ($artifactSessions.Count -eq 0) {
            Write-Host 'cam001: FAILED_no_decoded_session'
            exit 2
        }
        $battleSessionId = [string]$artifactSessions[0].session.battleSessionId
        $groundTruthSelection = 'launch-artifact-newest-decode'
    }

    $trajectory = Invoke-OdApi -Method 'Get' -RelativePath (
        '/api/v1/game/discover/trajectory/' + $battleSessionId)
    $viewpoints = @($trajectory.entities | Where-Object {
        $_.isViewpoint -eq $true -and $null -ne $_.entityId
    })
    if ($viewpoints.Count -ne 1 -or $null -eq $viewpoints[0].samples -or
        $viewpoints[0].samples.Count -lt 2) {
        Write-Host 'cam001: FAILED_viewpoint_ground_truth'
        exit 2
    }
    $viewpoint = $viewpoints[0]
    $viewpointEntityId = [int]$viewpoint.entityId
}
catch {
    Write-Host 'cam001: FAILED_ground_truth_api'
    exit 2
}

Write-Host ('cam001: ground_truth_selection=' + $groundTruthSelection +
    ' viewpoint_entity_bound=true samples=' + $viewpoint.samples.Count)

# Pre-fetch the decoded camera-yaw timeline (step 10 s) for the v5 yaw
# alignment. The memory camera yaw is matched against this timeline to find
# the decoded time it corresponds to (the bridge into decoded space for the
# W2S path). The position correlation itself is done in memory space via
# /discover/entity-position and needs no decoded-clock alignment.
$decodedYawTimeline = @()
try {
    $decodedDuration = [int]($trajectory.durationTicks / 10000000)
    for ($t = 0; $t -le $decodedDuration; $t += 10) {
        $frame = Invoke-OdApi -Method 'Get' -RelativePath (
            '/api/v1/sessions/' + $battleSessionId + '/frame?timeSeconds=' + $t)
        if ($null -ne $frame -and $null -ne $frame.cameraYawRadians) {
            $decodedYawTimeline += @{
                t   = $t
                yaw = [double]$frame.cameraYawRadians
            }
        }
    }
    Write-Host ('cam001: decoded_yaw_timeline samples=' + $decodedYawTimeline.Count)
}
catch {
    Write-Host 'cam001: decoded_yaw_timeline_unavailable (yaw alignment will be skipped)'
}

if ($StageDelaySeconds -gt 0) {
    Write-Host ('cam001: stage_delay_s=' + $StageDelaySeconds)
    Start-Sleep -Seconds $StageDelaySeconds
}

# ---- Memory walk -----------------------------------------------------------
# ASLR is enabled on this build, so the anchor dword = runtime module base +
# vftable RVA (0x3277e8c replay / 0x3277da4 live). The pattern scan response
# carries the main-module base address; compute the base-relative pattern
# and rescan if the preferred-base probe found nothing.
$replayVftableRva = 0x3277e8c        # avatar controller (replay mode)
$liveVftableRva = 0x3277da4           # avatar controller (live mode)
$cameraReplayVftableRva = 0x326dd0c   # ReplayCameraController (replay mode)
$cameraLiveVftableRva = 0x32de028     # CameraController (live mode)
$cameraStateVftableRva = 0x32dafa0    # GameCamera (pose owner)
$preferredImageBase = 0x400000

$chainOffsets = @(
    @{ Name = 'battleResources'; Displacement = 0x154; Kind = 'UInt32' },
    @{ Name = 'camera';          Displacement = 0x2C;  Kind = 'UInt32' },
    @{ Name = 'cameraState';     Displacement = 0x28;  Kind = 'UInt32' }
)

$candidates = @()
$anchorAddress = 0L
$chain = @()
$cameraFields = @{}
$roundSamples = @()
$scanAttempts = 0
$roundsFiniteAllFields = 0
$roundsYawCorrelated = 0
$roundsPositionCorrelated = 0
$basisFinite = $false
$yawCorrelated = $false
$positionCorrelated = $false
$yawDeltaRadians = $null
$alignedDecodedSeconds = $null
$offsetNorm = $null
$offsetNormC = $null
$memoryTankSource = ''
$entityPositionStatus = ''
$memoryDecodedDelta = $null
$cameraVftableMatches = $false
$cameraStateVftableHex = $null
$cameraStateIdentityMatches = $false
$memYaw = $null
$memPitch = $null
$calSample = $null

for ($round = 0; $round -lt $ReadCount; $round++) {
    try {
        # 1. Anchor scan (base-relative). Probe with the preferred-base
        #    pattern first: when ASLR is off this succeeds immediately; when
        #    on, the probe's response still reports the runtime module base
        #    and we rescan with (base + RVA).
        if ($scanAttempts -eq 0 -or -not $candidates.Count -ge 1) {
            $avatarVftableHex = Get-LittleEndianHex `
                -Value ([uint32]($preferredImageBase + $replayVftableRva))
            $scan = Invoke-OdApi -Method 'Post' `
                -RelativePath '/api/v1/game/discover/pattern' `
                -Body @{
                    fieldName        = 'avatar-vftable'
                    fieldType        = 'Bytes'
                    expectedValueHex = $avatarVftableHex
                    maxCandidates    = $MaxCandidates
                    minRegionSize    = 4096
                    alignment        = 4
                }
            $candidates = @($scan.candidates)
            $scanAttempts += 1
            Write-Host ('cam001: anchor_scan_round=' + $scanAttempts +
                ' candidates=' + $candidates.Count +
                ' base=' + [string]$scan.baseAddress)

            if ($candidates.Count -lt 1 -and
                -not [string]::IsNullOrWhiteSpace([string]$scan.baseAddress)) {
                # ASLR probe: recompute against the runtime module base.
                $runtimeBase = [long][string]$scan.baseAddress
                $avatarVftableHex = Get-LittleEndianHex `
                    -Value ([uint32]($runtimeBase + $replayVftableRva))
                $scan = Invoke-OdApi -Method 'Post' `
                    -RelativePath '/api/v1/game/discover/pattern' `
                    -Body @{
                        fieldName        = 'avatar-vftable'
                        fieldType        = 'Bytes'
                        expectedValueHex = $avatarVftableHex
                        maxCandidates    = $MaxCandidates
                        minRegionSize    = 4096
                        alignment        = 4
                    }
                $candidates = @($scan.candidates)
                $scanAttempts += 1
                Write-Host ('cam001: anchor_scan_aslr_rescan candidates=' +
                    $candidates.Count)
            }
        }

        # The scan response carries the main-module base even when ASLR is
        # on (probe round). Hoist it once; the identity gate below compares
        # the camera vftable against (runtime base + RVA).
        $runtimeBase = if ([string]::IsNullOrWhiteSpace([string]$scan.baseAddress)) {
            $preferredImageBase
        }
        else {
            [long][string]$scan.baseAddress
        }

        if ($candidates.Count -lt 1) {
            Write-Host 'cam001: FAILED_anchor_not_found'
            exit 3
        }

        # Pick the first candidate and walk the fixed chain.
        $anchorAddress = [long]$candidates[0].absoluteAddress

        $currentAddress = $anchorAddress
        $chain = @()
        $chainValid = $true
        foreach ($hop in $chainOffsets) {
            # Chain walk: read at (current + displacement); the pointer VALUE
            # becomes the next base (CORRECTED 2026-08-11 — the first live run
            # read at the bare current address and added the displacement to
            # the pointer value, dereferencing .rdata text instead of walking
            # the object chain).
            $readAddress = $currentAddress + $hop.Displacement
            $read = Invoke-OdApi -Method 'Post' `
                -RelativePath '/api/v1/game/discover/read' `
                -Body @{
                    addresses = @(('0x' + $readAddress.ToString('X')))
                    valueKind = $hop.Kind
                    valueSize = 4
                }
            $item = $read.reads[0]
            if ($null -eq $item -or -not [bool]$item.readOk -or
                [string]::IsNullOrWhiteSpace([string]$item.observedValueHex)) {
                $chainValid = $false
                Write-Host ('cam001: chain_hop_failed ' + $hop.Name)
                break
            }
            # observedValueHex is memory-order (little-endian) raw bytes.
            $ptrBytes = Convert-HexToBytes -Hex ([string]$item.observedValueHex)
            if ($ptrBytes.Length -ne 4) {
                $chainValid = $false
                Write-Host ('cam001: chain_hop_bad_hex ' + $hop.Name)
                break
            }
            $ptr = [BitConverter]::ToUInt32($ptrBytes, 0)
            $chain += [ordered]@{
                name  = $hop.Name
                value = $ptr
            }
            $currentAddress = [long]$ptr
        }

        if (-not $chainValid) {
            Write-Host 'cam001: FAILED_chain_walk'
            exit 3
        }

        $cameraAddress = [long]$chain[1].value      # hop 2: camera controller
        $cameraStateAddress = [long]$chain[2].value # hop 3: GameCamera ring

        # 1b. Identity gates: the camera controller must be the
        #     ReplayCameraController (or live CameraController) and the
        #     pose object must be the GameCamera (base+0x32dafa0) for the
        #     walk to be trustworthy.
        $identityRead = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/read' `
            -Body @{
                addresses = @(
                    ('0x' + $cameraAddress.ToString('X')),
                    ('0x' + $cameraStateAddress.ToString('X')))
                valueKind = 'UInt32'
                valueSize = 4
            }
        $cameraVftable = $null
        $cameraStateVftable = $null
        if ($null -ne $identityRead.reads[0] -and [bool]$identityRead.reads[0].readOk) {
            $b = Convert-HexToBytes -Hex ([string]$identityRead.reads[0].observedValueHex)
            if ($b.Length -eq 4) { $cameraVftable = [BitConverter]::ToUInt32($b, 0) }
        }
        if ($null -ne $identityRead.reads[1] -and [bool]$identityRead.reads[1].readOk) {
            $b = Convert-HexToBytes -Hex ([string]$identityRead.reads[1].observedValueHex)
            if ($b.Length -eq 4) { $cameraStateVftable = [BitConverter]::ToUInt32($b, 0) }
        }
        if ($null -eq $cameraVftable) {
            Write-Host 'cam001: FAILED_camera_identity_read'
            exit 3
        }
        $cameraVftableMatches = ($cameraVftable -eq [uint32]($runtimeBase + $cameraReplayVftableRva)) -or
            ($cameraVftable -eq [uint32]($runtimeBase + $cameraLiveVftableRva))
        $cameraStateVftableHex = if ($null -eq $cameraStateVftable) {
            $null
        }
        else {
            ('0x' + $cameraStateVftable.ToString('X8'))
        }
        $cameraStateIdentityMatches = $null -ne $cameraStateVftable -and
            ($cameraStateVftable -eq [uint32]($runtimeBase + $cameraStateVftableRva))
        if (-not $cameraVftableMatches) {
            Write-Host 'cam001: FAILED_camera_identity_mismatch'
            exit 3
        }

        # 2. GameCamera pose reads (v5): the live pose fields all sit on
        #    the GameCamera object at the diff-scan-verified offsets.
        $fieldSpecs = @(
            @{ Name = 'posAX';  Off = 0x38 },  # primary position
            @{ Name = 'posAY';  Off = 0x3C },
            @{ Name = 'posAZ';  Off = 0x40 },
            @{ Name = 'posBX';  Off = 0x44 },  # interpolated prev copy
            @{ Name = 'posBY';  Off = 0x48 },
            @{ Name = 'posBZ';  Off = 0x4C },
            @{ Name = 'yawCos'; Off = 0x50 },  # yaw = atan2(sin, cos)
            @{ Name = 'yawSin'; Off = 0x54 },
            @{ Name = 'pitch';  Off = 0x58 },
            @{ Name = 'basisA'; Off = 0x80 },  # view basis rows
            @{ Name = 'basisB'; Off = 0x84 },
            @{ Name = 'basisC'; Off = 0x88 },
            @{ Name = 'basisD'; Off = 0x90 },
            @{ Name = 'basisE'; Off = 0x94 },
            @{ Name = 'basisF'; Off = 0x98 },
            @{ Name = 'posCX';  Off = 0xB0 },  # extra position copy
            @{ Name = 'posCY';  Off = 0xB4 },
            @{ Name = 'posCZ';  Off = 0xB8 }
        )
        $readAddresses = @($fieldSpecs | ForEach-Object {
            ('0x' + ($cameraStateAddress + $_.Off).ToString('X'))
        })
        # CAM-001 v7 follow-up: the view-basis region +0x80..0xB0 (12
        # floats) is a row-major 3x4 view matrix (rows at +0x80/0x90/0xA0,
        # 16-byte stride — verified 2026-08-11: row0 = (fx,-fy,-fz) of the
        # yaw/pitch forward, rows orthonormal). Read all 12 so the offline
        # coherence check has the complete third row.
        $basisReadAddresses = @()
        for ($bo = 0x80; $bo -lt 0xB0; $bo += 4) {
            $basisReadAddresses += ('0x' + ($cameraStateAddress + $bo).ToString('X'))
        }
        $fieldRead = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/read' `
            -Body @{
                addresses = $readAddresses
                valueKind = 'Float'
                valueSize = 4
            }
        $fieldMap = @{}
        for ($f = 0; $f -lt $fieldSpecs.Count; $f++) {
            $item = $fieldRead.reads[$f]
            $floatValue = [double]::NaN
            if ($null -ne $item -and [bool]$item.readOk -and
                -not [string]::IsNullOrWhiteSpace([string]$item.observedValueHex)) {
                $bytes = Convert-HexToBytes -Hex ([string]$item.observedValueHex)
                if ($bytes.Length -eq 4) {
                    $floatValue = [double][single][BitConverter]::ToSingle($bytes, 0)
                }
            }
            $fieldMap[$fieldSpecs[$f].Name] = $floatValue
        }
        $cameraFields = $fieldMap

        # 3. Correlate against ground truth.
        $roundFinite = $true
        foreach ($name in @('posAX', 'posAY', 'posAZ', 'posBX', 'posBY', 'posBZ',
                'yawCos', 'yawSin', 'pitch',
                'basisA', 'basisB', 'basisC', 'basisD', 'basisE', 'basisF')) {
            if (-not (Test-Finite -Value $fieldMap[$name])) {
                $roundFinite = $false
            }
        }
        if ($roundFinite) {
            $roundsFiniteAllFields += 1
        }

        # Yaw: align the memory camera yaw (atan2 of the cos/sin pair)
        # against the pre-fetched decoded yaw timeline. This finds the
        # decoded time the camera corresponds to (the bridge into decoded
        # space for the W2S path).
        $roundYaw = $false
        $memYaw = $null
        $memPitch = $null
        if ((Test-Finite -Value $fieldMap['yawCos']) -and
            (Test-Finite -Value $fieldMap['yawSin'])) {
            $memYaw = [Math]::Atan2(
                [double]$fieldMap['yawSin'], [double]$fieldMap['yawCos'])
            $memPitch = [double]$fieldMap['pitch']
        }
        if ($null -ne $memYaw -and $decodedYawTimeline.Count -gt 0) {
            $bestDelta = [double]::MaxValue
            $bestT = $null
            foreach ($entry in $decodedYawTimeline) {
                $d = [Math]::Abs(([Math]::Atan2(
                    [Math]::Sin($memYaw - $entry.yaw),
                    [Math]::Cos($memYaw - $entry.yaw))))
                if ($d -lt $bestDelta) {
                    $bestDelta = $d
                    $bestT = $entry.t
                }
            }
            if ($null -ne $bestT) {
                # Record whenever the alignment runs (evidence stays
                # diagnosable even when out of range).
                $yawDeltaRadians = $bestDelta
                $alignedDecodedSeconds = $bestT
                if ($bestDelta -le 0.05) {
                    $roundYaw = $true
                }
            }
        }
        if ($roundYaw) {
            $roundsYawCorrelated += 1
        }

        # Position: camera-to-tank offset IN MEMORY SPACE. Prefer the
        # server /discover/entity-position; fall back to the direct
        # resolver-chain walk (v6: the endpoint's vtable gates are
        # phase-fragile; the direct walk has none). A third-person camera
        # sits ~1-30 m from the tank.
        $roundPosition = $false
        $tankX = $null
        $tankY = $null
        $tankZ = $null
        try {
            $entityPos = Invoke-OdApi -Method 'Post' `
                -RelativePath '/api/v1/game/discover/entity-position' `
                -Body @{
                    entityId = $viewpointEntityId
                    battleSessionId = $battleSessionId
                }
            if ([string]$entityPos.status -eq 'Resolved' -and
                $null -ne $entityPos.x -and $null -ne $entityPos.y -and
                $null -ne $entityPos.z) {
                $tankX = [double]$entityPos.x
                $tankY = [double]$entityPos.y
                $tankZ = [double]$entityPos.z
                $memoryTankSource = 'entity-position'
            }
            else {
                $entityPositionStatus = [string]$entityPos.status + ':' +
                    [string]$entityPos.failureStage
            }
        }
        catch {
            $entityPositionStatus = 'threw'
        }
        if ($null -eq $tankX) {
            $walked = Walk-EntityPositionMemory `
                -ModuleBase $runtimeBase -EntityId $viewpointEntityId
            if ($null -ne $walked) {
                $tankX = $walked.x
                $tankY = $walked.y
                $tankZ = $walked.z
                $memoryTankSource = 'direct-walk:' + $walked.source
            }
            else {
                $entityPositionStatus += '+direct-walk-failed'
            }
        }
        if ($null -ne $tankX) {
            $dx = [double]$fieldMap['posAX'] - $tankX
            $dy = [double]$fieldMap['posAY'] - $tankY
            $dz = [double]$fieldMap['posAZ'] - $tankZ
            $offsetNorm = [Math]::Sqrt(($dx * $dx) + ($dy * $dy) + ($dz * $dz))
            $dxc = [double]$fieldMap['posCX'] - $tankX
            $dyc = [double]$fieldMap['posCY'] - $tankY
            $dzc = [double]$fieldMap['posCZ'] - $tankZ
            $offsetNormC = [Math]::Sqrt(($dxc * $dxc) + ($dyc * $dyc) + ($dzc * $dzc))
            if (($offsetNorm -ge 1.0 -and $offsetNorm -le 30.0) -or
                ($offsetNormC -ge 1.0 -and $offsetNormC -le 30.0)) {
                $roundPosition = $true
            }
            # Coordinate calibration: memory tank vs decoded trajectory at
            # the yaw-aligned time. A constant delta across rounds = the
            # memory<->decoded origin offset for the W2S path.
            if ($null -ne $alignedDecodedSeconds) {
                $calTicks = [long]($alignedDecodedSeconds * 10000000)
                $calSample = $viewpoint.samples |
                    Sort-Object { [Math]::Abs([long]$_.replayTimeTicks - $calTicks) } |
                    Select-Object -First 1
                if ($null -ne $calSample) {
                    $cdx = $tankX - [double]$calSample.x
                    $cdy = $tankY - [double]$calSample.y
                    $cdz = $tankZ - [double]$calSample.z
                    $calNorm = [Math]::Sqrt(($cdx * $cdx) + ($cdy * $cdy) + ($cdz * $cdz))
                    if ($null -eq $memoryDecodedDelta -or $calNorm -lt $memoryDecodedDelta) {
                        $memoryDecodedDelta = $calNorm
                    }
                }
            }
        }

        # Per-round evidence for the W2S projection validator (v7): the
        # memory camera pose (posA + yaw/pitch), the memory tank (when
        # resolved), and the decoded tank at the yaw-aligned time. The
        # validator projects the decoded tank through the camera and
        # checks it lands near screen center (the third-person look-at
        # property).
        $roundDecodedTank = $null
        if ($null -ne $calSample) {
            $roundDecodedTank = @{
                x              = [double]$calSample.x
                y              = [double]$calSample.y
                z              = [double]$calSample.z
                replayTimeTicks = [long]$calSample.replayTimeTicks
            }
        }
        $roundSample = [ordered]@{
            camera = if ($null -ne $memYaw -and
                    (Test-Finite -Value $fieldMap['posAX']) -and
                    (Test-Finite -Value $fieldMap['posAY']) -and
                    (Test-Finite -Value $fieldMap['posAZ'])) {
                @{
                    x = [double]$fieldMap['posAX']
                    y = [double]$fieldMap['posAY']
                    z = [double]$fieldMap['posAZ']
                    yawRadians   = $memYaw
                    pitchRadians = $memPitch
                }
            }
            else {
                $null
            }
            memoryTank = if ($null -ne $tankX) {
                @{ x = [double]$tankX; y = [double]$tankY; z = [double]$tankZ }
            }
            else {
                $null
            }
            decodedTank       = $roundDecodedTank
            alignedDecodedSeconds = $alignedDecodedSeconds
            memoryTankSource  = $memoryTankSource
        }
        # CAM-001 v7 root-cause follow-up: persist the full view-basis region
        # (+0x80..0xB0, 12 floats — stride-4 3x4 view matrix verified
        # 2026-08-11) so the offline validator can check the walked object is
        # a COHERENT camera (orthonormal rows, row0 = (fx,-fy,-fz) of
        # yaw/pitch) — the memory-side half of the mode-vs-pose
        # discriminator. Additive; older consumers ignore unknown keys.
        $basisFloats = @()
        $basisRead = Invoke-OdApi -Method 'Post' `
            -RelativePath '/api/v1/game/discover/read' `
            -Body @{
                addresses = @($basisReadAddresses)
                valueKind = 'Float'
                valueSize = 4
            }
        foreach ($item in $basisRead.reads) {
            if ($null -ne $item -and [bool]$item.readOk -and
                -not [string]::IsNullOrWhiteSpace([string]$item.observedValueHex)) {
                $bytes = Convert-HexToBytes -Hex ([string]$item.observedValueHex)
                if ($bytes.Length -eq 4) {
                    $basisFloats += [double][single][BitConverter]::ToSingle($bytes, 0)
                    continue
                }
            }
            $basisFloats += [double]::NaN
        }
        $roundSample['basis'] = $basisFloats

        # CAM-001 v7 root-cause follow-up: when -CaptureWindow, capture the
        # game window in-memory and persist ONLY derived sky/terrain scalars.
        if ($CaptureWindow) {
            $screen = Get-CamWindowScalars
            if ($null -ne $screen) {
                $roundSample['screen'] = $screen
            }
        }
        $roundSamples += , $roundSample

        if ($roundPosition) {
            $roundsPositionCorrelated += 1
        }

        # View-basis sanity: all six floats finite (finite non-sentinel is
        # enough for the consistency claim; exact basis math is a later step).
        $roundBasisFinite = $true
        foreach ($name in @('basisA', 'basisB', 'basisC', 'basisD', 'basisE', 'basisF')) {
            if (-not (Test-Finite -Value $fieldMap[$name])) {
                $roundBasisFinite = $false
            }
        }
        if ($roundBasisFinite) {
            $basisFinite = $true
        }

        if ($round -lt ($ReadCount - 1)) {
            Start-Sleep -Milliseconds $ReadIntervalMilliseconds
        }
    }
    catch {
        Write-Host 'cam001: FAILED_memory_walk_api'
        exit 3
    }
}

$cameraIdentityVerified = $cameraVftableMatches -and $cameraStateIdentityMatches
$cameraStateVerified = $chain.Count -eq 3 -and $cameraIdentityVerified -and
    $roundsFiniteAllFields -eq $ReadCount -and $basisFinite
$yawCorrelated = $roundsYawCorrelated -ge 1
$positionCorrelated = $roundsPositionCorrelated -ge 1

$verdict = if ($cameraStateVerified -and $positionCorrelated) {
    'camera-state-consistent'
}
elseif ($cameraStateVerified) {
    'camera-state-found-unverified-offset'
}
else {
    'inconclusive'
}

$aggregate = [ordered]@{
    schema                = 'wotbtreader.cam001.camera-state-verify.v7'
    roundSamples          = $roundSamples
    campaign              = 'cam-001-camera-state'
    completedAtUtc        = [DateTime]::UtcNow.ToString('o')
    verdict               = $verdict
    expectedGameVersion   = '11.19.0.10'
    anchor                = 'avatar-vftable-replay-rva-0x3277e8c'
    anchorScanAttempts    = $scanAttempts
    anchorCandidatesFound = $candidates.Count
    chainOffsets          = @($chainOffsets | ForEach-Object { $_.Name })
    chainResolved         = $chain.Count -eq 3
    roundsCompleted       = $ReadCount
    roundsAllFieldsFinite = $roundsFiniteAllFields
    roundsYawCorrelated   = $roundsYawCorrelated
    roundsPositionCorrelated = $roundsPositionCorrelated
    yawCorrelated         = $yawCorrelated
    positionCorrelated    = $positionCorrelated
    basisRowsFinite       = $basisFinite
    cameraVftableMatches  = $cameraVftableMatches
    cameraStateVftableHex = $cameraStateVftableHex
    cameraStateIdentityMatches = $cameraStateIdentityMatches
    yawDeltaRadians       = $yawDeltaRadians
    alignedDecodedSeconds = $alignedDecodedSeconds
    thirdPersonOffsetNorm = $offsetNorm
    extraPositionOffsetNorm = $offsetNormC
    memoryTankSource      = $memoryTankSource
    entityPositionStatus  = $entityPositionStatus
    memoryDecodedDelta    = $memoryDecodedDelta
    groundTruthBoundToLaunchArtifact = $true
    groundTruthSelection  = $groundTruthSelection
    cameraStateVerified   = $cameraStateVerified
    offsetTablePromotionReady = $false
    privacy = [ordered]@{
        entityIdsPersisted    = $false
        # roundSamples carry the per-round camera + decoded-tank world
        # positions by design (the W2S projection validator consumes them),
        # so coordinates ARE persisted - flagged honestly (v7, 2026-08-11).
        coordinatesPersisted  = $true
        # v7 follow-up: per-round view-basis floats (+0x80..0xB0) are
        # persisted for the offline coherence check; derived sky/terrain
        # scalars only when -CaptureWindow (never raw pixels).
        basisRowsPersisted    = $true
        screenScalarsPersisted = ([bool]$CaptureWindow)
        processAddressesPersisted = $false
        rawBytesPersisted     = $false
        capabilityPersisted   = $false
    }
}

Write-AggregateResult -Aggregate $aggregate
Write-Host ('cam001: verdict=' + $verdict +
    ' anchor_candidates=' + $candidates.Count +
    ' chain=' + $chain.Count + '/3' +
    ' camera_identity=' + $cameraIdentityVerified +
    ' finite=' + $roundsFiniteAllFields + '/' + $ReadCount +
    ' yaw_correlated_rounds=' + $roundsYawCorrelated +
    ' pos_correlated_rounds=' + $roundsPositionCorrelated +
    ' offset_norm=' + [string]$offsetNorm +
    ' extra_offset_norm=' + [string]$offsetNormC +
    ' tank_source=' + $memoryTankSource +
    ' aligned_s=' + [string]$alignedDecodedSeconds)
exit 0
