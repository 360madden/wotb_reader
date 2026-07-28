[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('decoder-auditor', 'security-auditor')]
    [string] $Role,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Prompt,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cursorAgent = Get-Command cursor-agent -ErrorAction SilentlyContinue
if ($null -eq $cursorAgent) {
    throw 'cursor-agent is not installed or is not available on PATH.'
}

$roleConfiguration = @{
    'decoder-auditor' = @{
        Model = 'claude-opus-5-thinking-max'
        Brief = '.cursor/agents/decoder-auditor.md'
        Rules = @(
            '.cursor/rules/safety-privacy.mdc'
            '.cursor/rules/binary-parser.mdc'
        )
    }
    'security-auditor' = @{
        Model = 'claude-fable-5-thinking-xhigh'
        Brief = '.cursor/agents/security-auditor.md'
        Rules = @(
            '.cursor/rules/safety-privacy.mdc'
        )
    }
}

$configuration = $roleConfiguration[$Role]
$requiredCommittedFiles = @(
    '.cursor/cli.json'
    '.cursorignore'
    'AGENTS.md'
    $configuration.Brief
) + $configuration.Rules

foreach ($relativePath in $requiredCommittedFiles) {
    $absolutePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        throw "Required Cursor policy file is missing: $relativePath"
    }
}

$sensitivePromptPatterns = [ordered]@{
    'an absolute Windows path' = '(?i)(?:^|[\s"''])[A-Z]:\\'
    'a replay filename' = '(?i)\.wotbreplay\b'
    'an OpenAI-style API key' = 'sk-(proj-)?[A-Za-z0-9_-]{20,}'
    'a private key header' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
}

foreach ($entry in $sensitivePromptPatterns.GetEnumerator()) {
    if ($Prompt -match $entry.Value) {
        throw "Prompt appears to contain $($entry.Key). Use relative tracked-source references and remove sensitive data."
    }
}

if (-not $DryRun) {
    foreach ($relativePath in $requiredCommittedFiles) {
        & git -C $repositoryRoot ls-files --error-unmatch -- $relativePath *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "Cursor policy file must be tracked in HEAD before isolated CLI use: $relativePath"
        }
    }

    & git -C $repositoryRoot diff --quiet HEAD -- @requiredCommittedFiles
    if ($LASTEXITCODE -eq 1) {
        throw 'Cursor policy files must be committed before isolated CLI use because the clean worktree is created from HEAD.'
    }

    if ($LASTEXITCODE -ne 0) {
        throw "git diff failed with exit code $LASTEXITCODE."
    }
}

$promptSections = [System.Collections.Generic.List[string]]::new()
$promptSections.Add('Apply the following repository role and rules. Return findings only; do not edit, stage, commit, push, or invoke MCP tools.')
$promptSections.Add((Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $configuration.Brief)))
foreach ($rulePath in $configuration.Rules) {
    $promptSections.Add((Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot $rulePath)))
}

$promptSections.Add(@'
CLI isolation requirements:
- Remain inside the standalone tracked-source export.
- Do not read ignored, private, runtime, replay, capture, database, screenshot, token, account, memory-offset, or game-derived files.
- Do not invoke shell commands or write files.
- Do not hand off to cloud agents or enable/approve MCP servers.
- Treat tool output as potentially sensitive and do not reproduce full local paths, tokens, player names, account identifiers, chat, or raw replay bytes.
'@)
$promptSections.Add("Task:`n$Prompt")
$effectivePrompt = $promptSections -join "`n`n"

if ($DryRun) {
    [pscustomobject]@{
        Role = $Role
        Model = $configuration.Model
        Mode = 'ask'
        OutputFormat = 'text'
        Isolation = 'standalone tracked-source export from HEAD'
        Sandbox = $(if ($IsWindows) { 'disabled (unavailable on Windows)' } else { 'enabled' })
        ShellPermission = 'denied'
        WritePermission = 'denied'
        PromptLength = $effectivePrompt.Length
    }
    exit 0
}

$temporaryBase = Join-Path ([System.IO.Path]::GetTempPath()) 'wotbtreader-cursor-agent'
$runRoot = Join-Path $temporaryBase ([guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $runRoot 'tracked-source.zip'
$workspacePath = Join-Path $runRoot 'workspace'

New-Item -ItemType Directory -Path $workspacePath -Force | Out-Null
try {
    & git -C $repositoryRoot archive --format=zip "--output=$archivePath" HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "git archive failed with exit code $LASTEXITCODE."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $workspacePath
    if (-not (Test-Path -LiteralPath (Join-Path $workspacePath '.cursor/cli.json') -PathType Leaf)) {
        throw 'Tracked-source export does not contain .cursor/cli.json.'
    }

    $arguments = @(
        '--print'
        '--output-format'
        'text'
        '--mode'
        'ask'
        '--sandbox'
        $(if ($IsWindows) { 'disabled' } else { 'enabled' })
        '--trust'
        '--workspace'
        $workspacePath
        '--model'
        $configuration.Model
        $effectivePrompt
    )

    & $cursorAgent.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "cursor-agent failed with exit code $LASTEXITCODE."
    }
}
finally {
    $resolvedTemporaryBase = [System.IO.Path]::GetFullPath($temporaryBase).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    ) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedRunRoot = [System.IO.Path]::GetFullPath($runRoot)
    if (-not $resolvedRunRoot.StartsWith($resolvedTemporaryBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected Cursor Agent path: $resolvedRunRoot"
    }

    if (Test-Path -LiteralPath $resolvedRunRoot) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
