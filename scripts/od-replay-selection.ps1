<#
.SYNOPSIS
    Replay-selection helpers for the OD offline-replay launcher. Functions
    only; dot-source from the launcher and its Pester smoke tests.

.DESCRIPTION
    The launcher's source-of-truth rule: play a TOP-LEVEL ORIGINAL from the
    game's replays folder, never a flat GUID stage clone and never a copy
    inside wotbtreader-staging. These are the pure path decisions behind that
    rule, extracted so the Pester gate can pin them without a game, host, or
    network.

    - Test-OdGuidStageFileName   32-hex + .wotbreplay = a flat GUID clone.
    - Select-OdReplay            newest human-named top-level replay; GUID
                                clones only as a last resort; never recurses
                                into wotbtreader-staging.
    - Test-OdReplayIsStagingCopy true when a path lives under the staging
                                folder (must never be the launch source).

    No side effects: these functions only read the filesystem and never write,
    launch, or delete anything.
#>

function Test-OdGuidStageFileName {
    param([string]$Name)
    return ($Name -match '^[0-9a-fA-F]{32}\.wotbreplay$')
}

function Select-OdReplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReplaysDir
    )
    if (-not (Test-Path -LiteralPath $ReplaysDir -PathType Container)) {
        return $null
    }
    $candidates = @(Get-ChildItem -LiteralPath $ReplaysDir -Filter '*.wotbreplay' -File -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        return $null
    }
    $originals = @($candidates | Where-Object {
        -not (Test-OdGuidStageFileName -Name $_.Name)
    })
    if ($originals.Count -gt 0) {
        return ($originals | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    }
    return ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

function Test-OdReplayIsStagingCopy {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReplayPath,
        [Parameter(Mandatory = $true)]
        [string]$StagingDir
    )
    $full = [IO.Path]::GetFullPath($ReplayPath)
    $stage = [IO.Path]::GetFullPath($StagingDir)
    return $full.StartsWith(
        $stage + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}
