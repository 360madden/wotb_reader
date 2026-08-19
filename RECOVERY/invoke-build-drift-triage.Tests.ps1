# Pester smoke tests for the RECOVERY build-drift triage script.
# Synthetic and deterministic: a fake executable file and a fake offset table
# in a temp directory exercise every documented exit code without a game
# install. ASCII-only, Windows PowerShell 5.1-compatible.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$triageScript = Join-Path $here 'invoke-build-drift-triage.ps1'

function New-TriageFixture {
    param([bool] $MatchingHash, [bool] $Malformed = $false)

    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ('triage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $exe = Join-Path $dir 'fake-wotblitz.exe'
    Set-Content -LiteralPath $exe -Value 'fake executable bytes' -Encoding ASCII
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $recorded = if ($MatchingHash) { $hash } else { ('0' * 64) }
    $table = [pscustomobject]@{
        gameVersion      = '99.99.99.99'
        executableSha256 = $recorded
        chains           = [pscustomobject]@{
            playerPositionX = @(@{ kind = 'rootRva'; value = 1 })
            damageDealt     = @(@{ kind = 'vftableScan'; value = 2 })
        }
    }
    $tablePath = Join-Path $dir '99.99.99.99.json'
    if ($Malformed) {
        Set-Content -LiteralPath $tablePath -Value '{ not json' -Encoding ASCII
    }
    else {
        $table | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tablePath -Encoding ASCII
    }
    Set-Content -LiteralPath (Join-Path $dir 'schema.json') -Value '{ "schema": true }' -Encoding ASCII

    return [pscustomobject]@{
        Dir    = $dir
        Exe    = $exe
        Report = (Join-Path $dir 'report.json')
    }
}

function Invoke-Triage {
    param(
        [string] $Exe,
        [string] $OffsetDir,
        [string] $ReportPath
    )

    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $triageScript `
        -GameExePath $Exe -OffsetDir $OffsetDir -ReportPath $ReportPath -Quiet 2>&1
    return $LASTEXITCODE
}

Describe 'build-drift triage exit-code contract' {
    It 'exits 0 (same-build) when the installed hash matches the newest table' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $code = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $code | Should Be 0
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'exits 1 (drifted) when the installed hash matches no table' {
        $fixture = New-TriageFixture -MatchingHash $false
        try {
            $code = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $code | Should Be 1
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'exits 2 when the GameExePath does not exist' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $code = Invoke-Triage -Exe (Join-Path $fixture.Dir 'missing.exe') -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $code | Should Be 2
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'exits 3 when the offset directory does not exist' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $code = Invoke-Triage -Exe $fixture.Exe -OffsetDir (Join-Path $fixture.Dir 'no-such-dir') -ReportPath $fixture.Report
            $code | Should Be 3
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'exits 3 (read-error) when a table is malformed JSON' {
        $fixture = New-TriageFixture -MatchingHash $true -Malformed $true
        try {
            $code = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $code | Should Be 3
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'extracts rootRva and vftableScan anchor hops and skips schema.json' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $null = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $report = Get-Content -LiteralPath $fixture.Report -Raw | ConvertFrom-Json
            @($report.tables).Count | Should Be 1
            $row = $report.tables[0]
            @($row.fields).Count | Should Be 2
            @($row.anchors).Count | Should Be 2
            @($row.anchors | Where-Object { $_.kind -eq 'rootRva' }).Count | Should Be 1
            @($row.anchors | Where-Object { $_.kind -eq 'vftableScan' }).Count | Should Be 1
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'writes a report with the documented shape' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $null = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $report = Get-Content -LiteralPath $fixture.Report -Raw | ConvertFrom-Json
            $report.verdict | Should Be 'same-build'
            $report.exitCode | Should Be 0
            $report.tool | Should Match 'invoke-build-drift-triage.ps1'
            $report.playbook | Should Match 'build-drift-recovery.md'
            $report.newestTable | Should Be '99.99.99.99'
            $report.exe.sha256 | Should Be ((Get-FileHash -LiteralPath $fixture.Exe -Algorithm SHA256).Hash.ToLowerInvariant())
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }

    It 'writes the report as BOM-less UTF-8 (machine-readable JSON)' {
        $fixture = New-TriageFixture -MatchingHash $true
        try {
            $null = Invoke-Triage -Exe $fixture.Exe -OffsetDir $fixture.Dir -ReportPath $fixture.Report
            $bytes = [System.IO.File]::ReadAllBytes($fixture.Report)
            if ($bytes.Length -ge 3) {
                ($bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) | Should Be $true
            }
        }
        finally {
            Remove-Item -LiteralPath $fixture.Dir -Recurse -Force
        }
    }
}
