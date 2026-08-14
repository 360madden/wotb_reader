<#
.SYNOPSIS
    Persisted replay-completion marker (OD-099 durable fix). Functions only;
    dot-source this file from the launcher, clicker, driver, and chain.

.DESCRIPTION
    The cross-session completion signal -- the Host gate Denied with reason
    evidence.replay_completed -- is IN-MEMORY and dies with the game process:
    the game exits on its own ~1-2 min after the Battle Results screen (no
    crash, no shutdown lines, replay file untouched; nothing in the
    launcher/clicker/driver/chain closes it -- OD-RECOVERY-099 forensics,
    2026-08-12). This helper persists completion as a marker file so a
    re-run of the SAME replay fails fast with FAILED_replay_already_completed
    instead of launching the game again.

    Marker keying is the replay file's FINGERPRINT (full path + size +
    LastWriteTimeUtc). Replay files are immutable in this workflow, so a
    matching fingerprint proves the marker belongs to THIS replay; a replay
    that was re-imported/replaced (fingerprint mismatch) or deleted is NOT
    treated as completed. A corrupt/unreadable marker is ignored (fail-open
    to a fresh run), never a false block.

    Marker location: $env:LOCALAPPDATA\WotBTreader\od-completion\
    <sha256-of-lowercased-full-path>.json, owner-only ACL via icacls (the
    BLK-0026 pattern: Set-Acl throws PrivilegeNotHeldException on an already
    protected owner-only ACL; icacls /inheritance:r /grant:r does not).

.EXAMPLE
    . (Join-Path $PSScriptRoot 'od-replay-completion.ps1')
    if (Test-OdReplayCompleted -ReplayPath $ReplayPath) {
        Write-Host 'FAILED_replay_already_completed'
        exit 2
    }
    # ... after the in-session teardown is observed ...
    Write-OdCompletionMarker -ReplayPath $ReplayPath -Reason 'in-session teardown' -SessionId $SessionId
#>

function Get-OdCompletionMarkerDirectory {
    return (Join-Path $env:LOCALAPPDATA 'WotBTreader\od-completion')
}

function Get-OdReplayFullPath([string]$ReplayPath) {
    return [IO.Path]::GetFullPath($ReplayPath)
}

function Get-OdCompletionMarkerPath([string]$ReplayPath) {
    $full = Get-OdReplayFullPath -ReplayPath $ReplayPath
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($full.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }
    $hex = ($hash | ForEach-Object { $_.ToString('x2') }) -join ''
    return (Join-Path (Get-OdCompletionMarkerDirectory) ($hex + '.json'))
}

function Get-OdReplayFingerprint([string]$ReplayPath) {
    if (-not (Test-Path -LiteralPath $ReplayPath)) { return $null }
    $item = Get-Item -LiteralPath $ReplayPath -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return @{
        Size         = $item.Length
        LastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}

function Test-OdOwnerOnlyFileAcl([string]$Path) {
    try {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = Get-Acl -LiteralPath $Path
        $observedOwner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $rules = @($acl.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        return $acl.AreAccessRulesProtected -and $observedOwner -eq $owner -and
            $rules.Count -eq 1 -and $rules[0].IdentityReference -eq $owner -and
            $rules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl)
    }
    catch {
        return $false
    }
}

function Test-OdOwnerOnlyDirectoryAcl([string]$Path) {
    try {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $directory = Get-Item -LiteralPath $Path
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $false
        }
        $acl = Get-Acl -LiteralPath $directory.FullName
        $observedOwner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate(
            [Security.Principal.SecurityIdentifier])
        $rules = @($acl.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        return $acl.AreAccessRulesProtected -and $observedOwner -eq $owner -and
            $rules.Count -eq 1 -and $rules[0].IdentityReference -eq $owner -and
            $rules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($rules[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl)
    }
    catch {
        return $false
    }
}

function Confirm-OdOwnerOnlyFileAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [ValidateRange(1, 10)]
        [int]$MaxAttempts = 5,
        [ValidateRange(0, 1000)]
        [int]$DelayMilliseconds = 100
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (Test-OdOwnerOnlyFileAcl -Path $Path) {
            return $true
        }
        if ($attempt -lt $MaxAttempts -and $DelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }

    return $false
}

function Confirm-OdOwnerOnlyDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [ValidateRange(1, 10)]
        [int]$MaxAttempts = 5,
        [ValidateRange(0, 1000)]
        [int]$DelayMilliseconds = 100
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (Test-OdOwnerOnlyDirectoryAcl -Path $Path) {
            return $true
        }
        if ($attempt -lt $MaxAttempts -and $DelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }

    return $false
}

function Set-OdOwnerOnlyFileAcl([string]$Path) {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    # icacls instead of .NET Set-Acl (BLK-0026 root cause): Set-Acl throws
    # PrivilegeNotHeldException on a protected owner-only ACL; icacls
    # /inheritance:r disables inherited ACEs and /grant:r replaces grants
    # with exactly the single owner FullControl rule.
    & icacls $Path /inheritance:r /grant:r ("*" + $owner + ':F') | Out-Null
}

function Set-OdOwnerOnlyDirectoryAcl([string]$Path) {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    # (OI)(CI) propagates the owner-only rule to children so future marker
    # files inherit it.
    & icacls $Path /inheritance:r /grant:r ("*" + $owner + ':(OI)(CI)F') | Out-Null
}

function Write-OdCompletionMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReplayPath,
        [Parameter(Mandatory = $true)]
        [string]$Reason,
        [string]$SessionId = ''
    )
    # NEVER THROW: every caller treats a failed marker write as non-fatal
    # (the driver's verdict must still run; the launcher's clean exit codes
    # must not be masked by FAILED_unexpected). New-Item / WriteAllText /
    # icacls can all throw on permissions or IO faults - catch and report
    # $false so the call site decides.
    try {
        $dir = Get-OdCompletionMarkerDirectory
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Set-OdOwnerOnlyDirectoryAcl -Path $dir
        if (-not (Confirm-OdOwnerOnlyDirectoryAcl -Path $dir)) {
            return $false
        }
        $fingerprint = Get-OdReplayFingerprint -ReplayPath $ReplayPath
        if ($null -eq $fingerprint) {
            return $false
        }
        $marker = @{
            version       = 1
            replayPath    = (Get-OdReplayFullPath -ReplayPath $ReplayPath)
            replaySize    = $fingerprint.Size
            replayLastUtc = $fingerprint.LastWriteUtc
            completedAtUtc = [DateTime]::UtcNow.ToString('o')
            reason        = $Reason
            sessionId     = $SessionId
        } | ConvertTo-Json
        $markerPath = Get-OdCompletionMarkerPath -ReplayPath $ReplayPath
        [IO.File]::WriteAllText(
            $markerPath,
            $marker,
            (New-Object Text.UTF8Encoding($false)))
        Set-OdOwnerOnlyFileAcl -Path $markerPath
        if (-not (Confirm-OdOwnerOnlyFileAcl -Path $markerPath)) {
            Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
            return $false
        }
        return $true
    }
    catch {
        return $false
    }
}

function Test-OdReplayCompleted {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReplayPath
    )
    # NEVER THROW: the pre-flight contract is fail-open on ANY unreadable/
    # corrupt marker (the docstring's "ignored, never a false block"). A
    # JSON that parses but has wrong types (e.g. replaySize as a string), a
    # missing field under StrictMode, or a [long] cast failure must all
    # return $false - not throw into the caller's FAILED_unexpected path.
    try {
        $markerPath = Get-OdCompletionMarkerPath -ReplayPath $ReplayPath
        if (-not (Test-Path -LiteralPath $markerPath)) {
            return $false
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
        if ($null -eq $marker -or $null -eq $marker.replayPath) {
            return $false
        }
        $fingerprint = Get-OdReplayFingerprint -ReplayPath $ReplayPath
        if ($null -eq $fingerprint) {
            # Replay file gone: nothing to launch; a re-import produces a new file.
            return $false
        }
        $full = Get-OdReplayFullPath -ReplayPath $ReplayPath
        return [string]::Equals(
                [string]$marker.replayPath, $full,
                [StringComparison]::OrdinalIgnoreCase) -and
            ([long]$marker.replaySize -eq [long]$fingerprint.Size) -and
            ([string]$marker.replayLastUtc -eq [string]$fingerprint.LastWriteUtc)
    }
    catch {
        # Corrupt marker (unparseable or wrong-typed): fail-open, never a
        # false block and never a throw into the caller.
        return $false
    }
}
