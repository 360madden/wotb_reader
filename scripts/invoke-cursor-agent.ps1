[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('decoder-auditor', 'security-auditor')]
    [string] $Role,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Prompt,

    [ValidateSet('text', 'json', 'stream-json')]
    [string] $OutputFormat = 'text',

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

if (-not $DryRun) {
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
- Remain inside the isolated worktree.
- Do not read ignored, private, runtime, replay, capture, database, screenshot, token, account, memory-offset, or game-derived files.
- Do not invoke cmd.exe or repository .cmd/.bat wrappers.
- Do not hand off to cloud agents or enable/approve MCP servers.
- Treat tool output as potentially sensitive and do not reproduce full local paths, tokens, player names, account identifiers, chat, or raw replay bytes.
'@)
$promptSections.Add("Task:`n$Prompt")
$effectivePrompt = $promptSections -join "`n`n"

$worktreeName = "wotb-$Role-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID"
$arguments = @(
    '--print'
    '--output-format'
    $OutputFormat
    '--mode'
    'ask'
    '--sandbox'
    $(if ($IsWindows) { 'disabled' } else { 'enabled' })
    '--trust'
    '--worktree'
    $worktreeName
    '--worktree-base'
    'HEAD'
    '--skip-worktree-setup'
    '--model'
    $configuration.Model
    $effectivePrompt
)

if ($DryRun) {
    [pscustomobject]@{
        Role = $Role
        Model = $configuration.Model
        Mode = 'ask'
        OutputFormat = $OutputFormat
        Isolation = 'clean worktree from HEAD'
        Sandbox = $(if ($IsWindows) { 'disabled (unavailable on Windows)' } else { 'enabled' })
        PromptLength = $effectivePrompt.Length
    }
    exit 0
}

Push-Location $repositoryRoot
try {
    & $cursorAgent.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "cursor-agent failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
