# Pester smoke tests for the persisted replay-completion marker (OD-099).
#
# These pin the helper's three documented contracts so a regression cannot
# slip past the gate again:
#   1. NEVER-THROW  -- a missing replay, missing marker, corrupt marker, or
#      wrong-typed marker returns $false instead of throwing into a caller's
#      FAILED_unexpected path.
#   2. FAIL-OPEN    -- an unreadable/corrupt marker never blocks a fresh run.
#   3. CLEAN-RUN    -- a written marker is recognized for the same replay and
#      NOT recognized once the replay's fingerprint changes.
#
# The marker is keyed on the full path's fingerprint, so these tests use a
# unique temp replay path that can never collide with a real replay; every
# marker written here is removed in AfterEach/AfterAll. The helper's marker
# directory ($env:LOCALAPPDATA\WotBTreader\od-completion) is shared with real
# sessions by design -- only this temp path's marker is ever touched.

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'od-replay-completion.ps1')

# Script-scope state: Pester 3.x runs BeforeAll/AfterAll in a scope that does
# not see Describe-body variables, so shared fixtures live at script scope.
$script:tempReplay = Join-Path ([IO.Path]::GetTempPath()) (
    'od-completion-smoke-' + [guid]::NewGuid().ToString('N') + '.wotbreplay')
$script:canonicalBytes = [byte[]](1..255)

Describe 'od-replay-completion smoke tests' {
    BeforeEach {
        [IO.File]::WriteAllBytes($script:tempReplay, $script:canonicalBytes)
    }

    AfterEach {
        $marker = Get-OdCompletionMarkerPath -ReplayPath $script:tempReplay
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    }

    AfterAll {
        Remove-Item -LiteralPath $script:tempReplay -Force -ErrorAction SilentlyContinue
        $marker = Get-OdCompletionMarkerPath -ReplayPath $script:tempReplay
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    }

    It 'returns false (no throw) for a replay with no marker' {
        Test-OdReplayCompleted -ReplayPath $script:tempReplay | Should Be $false
    }

    It 'returns false (no throw) for a non-existent replay' {
        $missing = Join-Path ([IO.Path]::GetTempPath()) (
            'od-completion-smoke-missing-' + [guid]::NewGuid().ToString('N') + '.wotbreplay')
        Test-OdReplayCompleted -ReplayPath $missing | Should Be $false
    }

    It 'fails open on a corrupt marker' {
        $marker = Get-OdCompletionMarkerPath -ReplayPath $script:tempReplay
        [IO.Directory]::CreateDirectory((Split-Path -Parent $marker)) | Out-Null
        [IO.File]::WriteAllText($marker, 'not-json {', (New-Object Text.UTF8Encoding($false)))
        Test-OdReplayCompleted -ReplayPath $script:tempReplay | Should Be $false
    }

    It 'fails open on a wrong-typed marker' {
        $marker = Get-OdCompletionMarkerPath -ReplayPath $script:tempReplay
        [IO.Directory]::CreateDirectory((Split-Path -Parent $marker)) | Out-Null
        $full = Get-OdReplayFullPath -ReplayPath $script:tempReplay
        $bad = @{
            version       = 1
            replayPath    = $full
            replaySize    = 'not-a-long'
            replayLastUtc = 'x'
        } | ConvertTo-Json
        [IO.File]::WriteAllText($marker, $bad, (New-Object Text.UTF8Encoding($false)))
        Test-OdReplayCompleted -ReplayPath $script:tempReplay | Should Be $false
    }

    It 'writes and recognizes a clean completion marker (round trip)' {
        Write-OdCompletionMarker -ReplayPath $script:tempReplay -Reason 'smoke' | Should Be $true
        Test-OdReplayCompleted -ReplayPath $script:tempReplay | Should Be $true
    }

    It 'does not recognize a replay whose fingerprint changed' {
        Write-OdCompletionMarker -ReplayPath $script:tempReplay -Reason 'smoke' | Should Be $true
        [IO.File]::WriteAllBytes($script:tempReplay, [byte[]](1..100))
        Test-OdReplayCompleted -ReplayPath $script:tempReplay | Should Be $false
    }

    It 'returns false (no throw) when writing a marker for a missing replay' {
        $missing = Join-Path ([IO.Path]::GetTempPath()) (
            'od-completion-smoke-missing-' + [guid]::NewGuid().ToString('N') + '.wotbreplay')
        Write-OdCompletionMarker -ReplayPath $missing -Reason 'smoke' | Should Be $false
    }

    It 'launcher checks the persisted marker before the CLI version probe' {
        $launcher = Join-Path $here 'launch-offline-replay-for-od.ps1'
        $source = Get-Content -LiteralPath $launcher -Raw
        $completionIndex = $source.IndexOf(
            'if (Test-OdReplayCompleted -ReplayPath $ReplayPath)',
            [StringComparison]::Ordinal)
        $probeIndex = $source.IndexOf(
            '$probeOut = & $cli probe',
            [StringComparison]::Ordinal)

        ($completionIndex -ge 0) | Should Be $true
        ($probeIndex -gt $completionIndex) | Should Be $true
    }

    It 'retries a transient directory ACL verification without weakening it' {
        $script:directoryAclChecks = 0
        Mock Test-OdOwnerOnlyDirectoryAcl {
            $script:directoryAclChecks++
            return $script:directoryAclChecks -ge 2
        }

        Confirm-OdOwnerOnlyDirectoryAcl `
            -Path $script:tempReplay -MaxAttempts 3 -DelayMilliseconds 0 |
            Should Be $true
        $script:directoryAclChecks | Should Be 2
    }

    It 'fails closed when a file ACL never verifies' {
        $script:fileAclChecks = 0
        Mock Test-OdOwnerOnlyFileAcl {
            $script:fileAclChecks++
            return $false
        }

        Confirm-OdOwnerOnlyFileAcl `
            -Path $script:tempReplay -MaxAttempts 3 -DelayMilliseconds 0 |
            Should Be $false
        $script:fileAclChecks | Should Be 3
    }

    It 'launcher confirms marker ACLs with the bounded retry helpers' {
        $launcher = Join-Path $here 'launch-offline-replay-for-od.ps1'
        $source = Get-Content -LiteralPath $launcher -Raw

        ($source.Contains(
                'Confirm-OdOwnerOnlyDirectoryAcl -Path $launchMarkerDirectory')) |
            Should Be $true
        ($source.Contains('Confirm-OdOwnerOnlyFileAcl -Path $launchMarker')) |
            Should Be $true
    }

    It 'reports only a bounded ACL condition for launcher diagnostics' {
        $missing = Join-Path ([IO.Path]::GetTempPath()) (
            'od-acl-diagnostic-missing-' + [guid]::NewGuid().ToString('N'))
        $diagnostic = Get-OdOwnerOnlyDirectoryAclDiagnostic -Path $missing

        $diagnostic | Should Match '^exception-[A-Za-z]+$'
        $diagnostic.Contains($missing) | Should Be $false
    }

    It 'reports the verified-after-window condition for the exact ACL' {
        $directory = Join-Path ([IO.Path]::GetTempPath()) (
            'od-acl-diagnostic-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $directory | Out-Null
        try {
            Set-OdOwnerOnlyDirectoryAcl -Path $directory

            Get-OdOwnerOnlyDirectoryAclDiagnostic -Path $directory |
                Should Be 'verified-after-retry-window'
        }
        finally {
            Remove-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue
        }
    }
}
