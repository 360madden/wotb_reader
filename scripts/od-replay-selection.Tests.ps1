# Pester smoke tests for the OD replay-selection + staging-refusal helpers.
#
# The launcher's source-of-truth rule is: play a TOP-LEVEL ORIGINAL from the
# game's replays folder, never a flat GUID stage clone and never a copy inside
# wotbtreader-staging. That rule is pure path logic extracted into
# od-replay-selection.ps1 so the gate can pin it without a game, host, or
# network. These tests pin the three documented contracts:
#   1. Test-OdGuidStageFileName  -- 32-hex + .wotbreplay is a flat GUID clone;
#      human-named, non-hex, or missing-extension names are not.
#   2. Select-OdReplay           -- newest HUMAN-named top-level replay wins;
#      GUID clones are only a last resort; nothing under wotbtreader-staging
#      is ever considered; empty/missing dirs return nothing.
#   3. Test-OdReplayIsStagingCopy -- true only for a path UNDER the staging
#      folder (never the folder itself or a sibling with a similar prefix).
#
# No side effects: every fixture lives under a unique temp dir and is removed
# in AfterAll. The real game replays folder is never touched.

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'od-replay-selection.ps1')

# Script-scope fixtures: Pester 3.x runs BeforeAll/AfterAll in a scope that
# does not see Describe-body variables, so shared fixtures live at script scope
# (same pattern as od-replay-completion.Tests.ps1).
$script:fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'od-replay-selection-smoke-' + [guid]::NewGuid().ToString('N'))
$script:replays = Join-Path $script:fixtureRoot 'replays'
$script:staging = Join-Path $script:replays 'wotbtreader-staging'

Describe 'od-replay-selection smoke tests' {
    BeforeAll {
        New-Item -ItemType Directory -Path $script:replays -Force | Out-Null
    }

    # Each file-fixture test must start from an empty replays dir and no
    # staging dir: Select-OdReplay returns the NEWEST matching file, so a
    # leftover from an earlier test would silently win and mask the test's
    # own fixture.
    BeforeEach {
        Get-ChildItem -LiteralPath $script:replays -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $script:staging) {
            Remove-Item -LiteralPath $script:staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    AfterAll {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Context 'Test-OdGuidStageFileName' {
        It 'recognizes a 32-hex GUID stage name' {
            Test-OdGuidStageFileName -Name '26268b58123456789abcdef012345678.wotbreplay' | Should Be $true
        }

        It 'recognizes uppercase hex' {
            Test-OdGuidStageFileName -Name 'ABCDEF0123456789ABCDEF0123456789.wotbreplay' | Should Be $true
        }

        It 'rejects a human-named original' {
            Test-OdGuidStageFileName -Name '20260802_1615_Churchill.wotbreplay' | Should Be $false
        }

        It 'rejects a 32-char name with non-hex characters' {
            Test-OdGuidStageFileName -Name 'zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.wotbreplay' | Should Be $false
        }

        It 'rejects a 32-hex name without the extension' {
            Test-OdGuidStageFileName -Name '26268b58123456789abcdef012345678' | Should Be $false
        }

        It 'rejects a name that only matches mid-string' {
            Test-OdGuidStageFileName -Name 'keep-26268b58123456789abcdef012345678.wotbreplay' | Should Be $false
        }
    }

    Context 'Select-OdReplay' {
        It 'returns nothing for a missing directory' {
            $result = Select-OdReplay -ReplaysDir (Join-Path $script:fixtureRoot 'nope')
            $result | Should BeNullOrEmpty
        }

        It 'returns nothing when no wotbreplay files exist' {
            $result = Select-OdReplay -ReplaysDir $script:replays
            $result | Should BeNullOrEmpty
        }

        It 'prefers the newest human-named replay over a GUID clone' {
            $clone = Join-Path $script:replays '26268b58123456789abcdef012345678.wotbreplay'
            $human = Join-Path $script:replays '20260802_1615_Churchill.wotbreplay'
            [IO.File]::WriteAllBytes($clone, [byte[]](1..8))
            [IO.File]::WriteAllBytes($human, [byte[]](1..8))
            (Get-Item -LiteralPath $clone).LastWriteTime = (Get-Date).AddMinutes(-1)
            (Get-Item -LiteralPath $human).LastWriteTime = (Get-Date)

            (Select-OdReplay -ReplaysDir $script:replays).Name |
                Should Be '20260802_1615_Churchill.wotbreplay'
        }

        It 'falls back to the newest GUID clone when no human-named replay exists' {
            $old = Join-Path $script:replays 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay'
            $new = Join-Path $script:replays 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.wotbreplay'
            [IO.File]::WriteAllBytes($old, [byte[]](1..8))
            [IO.File]::WriteAllBytes($new, [byte[]](1..8))
            (Get-Item -LiteralPath $old).LastWriteTime = (Get-Date).AddMinutes(-1)
            (Get-Item -LiteralPath $new).LastWriteTime = (Get-Date)

            (Select-OdReplay -ReplaysDir $script:replays).Name |
                Should Be 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.wotbreplay'
        }

        It 'never selects a staging copy even when it is newer' {
            New-Item -ItemType Directory -Path $script:staging -Force | Out-Null
            $staged = Join-Path $script:staging 'cccccccccccccccccccccccccccccccc.wotbreplay'
            $human = Join-Path $script:replays '20260802_1615_Medvedkovo.wotbreplay'
            [IO.File]::WriteAllBytes($staged, [byte[]](1..8))
            [IO.File]::WriteAllBytes($human, [byte[]](1..8))
            (Get-Item -LiteralPath $staged).LastWriteTime = (Get-Date)
            (Get-Item -LiteralPath $human).LastWriteTime = (Get-Date).AddMinutes(-1)

            (Select-OdReplay -ReplaysDir $script:replays).Name |
                Should Be '20260802_1615_Medvedkovo.wotbreplay'
        }
    }

    Context 'Test-OdReplayIsStagingCopy' {
        It 'flags a path inside the staging folder' {
            Test-OdReplayIsStagingCopy `
                -ReplayPath (Join-Path $script:staging 'x.wotbreplay') `
                -StagingDir $script:staging | Should Be $true
        }

        It 'does not flag the staging folder itself' {
            Test-OdReplayIsStagingCopy `
                -ReplayPath $script:staging `
                -StagingDir $script:staging | Should Be $false
        }

        It 'does not flag a top-level replay' {
            Test-OdReplayIsStagingCopy `
                -ReplayPath (Join-Path $script:replays 'x.wotbreplay') `
                -StagingDir $script:staging | Should Be $false
        }

        It 'does not flag a sibling directory with a similar prefix' {
            $sibling = Join-Path $script:replays 'wotbtreader-staging2'
            New-Item -ItemType Directory -Path $sibling -Force | Out-Null
            Test-OdReplayIsStagingCopy `
                -ReplayPath (Join-Path $sibling 'x.wotbreplay') `
                -StagingDir $script:staging | Should Be $false
        }

        It 'matches case-insensitively' {
            $upper = $script:staging.ToUpper()
            Test-OdReplayIsStagingCopy `
                -ReplayPath (Join-Path $upper 'x.wotbreplay') `
                -StagingDir $script:staging | Should Be $true
        }
    }
}
