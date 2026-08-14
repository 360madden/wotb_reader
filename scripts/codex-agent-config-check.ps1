#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot '..'
}

$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$codexRoot = Join-Path $repo '.codex'
$projectConfig = Join-Path $codexRoot 'config.toml'
$agentsRoot = Join-Path $codexRoot 'agents'
$hooksPath = Join-Path $codexRoot 'hooks.json'
$hookScript = Join-Path $codexRoot 'hooks\enforce-sol-model.ps1'
$agentsGuide = Join-Path $repo 'AGENTS.md'
$allowedModel = 'gpt-5.6-sol'

function Get-RequiredText {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required Codex policy file is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw
}

function Assert-UniqueStringAssignment {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $Expected,
        [Parameter(Mandatory)][string] $Source
    )

    $pattern = '(?m)^\s*' + [regex]::Escape($Key) +
        '\s*=\s*"([^"\r\n]+)"\s*(?:#.*)?$'
    $assignmentMatches = [regex]::Matches($Text, $pattern)
    if ($assignmentMatches.Count -ne 1) {
        throw "$Source must define exactly one $Key string assignment."
    }

    if ($assignmentMatches[0].Groups[1].Value -ne $Expected) {
        throw "$Source sets $Key='$($assignmentMatches[0].Groups[1].Value)'; expected '$Expected'."
    }
}

function Assert-UniqueRawAssignment {
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Key,
        [Parameter(Mandatory)][string] $Expected,
        [Parameter(Mandatory)][string] $Source
    )

    $pattern = '(?m)^\s*' + [regex]::Escape($Key) +
        '\s*=\s*([^#\r\n]+?)\s*(?:#.*)?$'
    $assignmentMatches = [regex]::Matches($Text, $pattern)
    if ($assignmentMatches.Count -ne 1) {
        throw "$Source must define exactly one $Key assignment."
    }

    if ($assignmentMatches[0].Groups[1].Value.Trim() -ne $Expected) {
        throw "$Source sets $Key='$($assignmentMatches[0].Groups[1].Value.Trim())'; expected '$Expected'."
    }
}

$configText = Get-RequiredText -Path $projectConfig
Assert-UniqueStringAssignment $configText 'model' $allowedModel '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'model_reasoning_effort' 'medium' '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'plan_mode_reasoning_effort' 'xhigh' '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'model_reasoning_summary' 'concise' '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'model_verbosity' 'low' '.codex/config.toml'
Assert-UniqueRawAssignment $configText 'enabled' 'true' '.codex/config.toml'
Assert-UniqueRawAssignment $configText 'max_concurrent_threads_per_session' '2' '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'default_subagent_model' $allowedModel '.codex/config.toml'
Assert-UniqueStringAssignment $configText 'default_subagent_reasoning_effort' 'medium' '.codex/config.toml'

$expectedRoles = [ordered]@{
    default          = 'medium'
    worker           = 'medium'
    explorer         = 'low'
    systems_analyst  = 'high'
    strategist       = 'xhigh'
    memory_researcher = 'max'
    verifier         = 'low'
    implementer_glue = 'medium'
    decoder_auditor  = 'high'
    security_auditor = 'xhigh'
}

$agentFiles = @(Get-ChildItem -LiteralPath $agentsRoot -File -Filter '*.toml')
if ($agentFiles.Count -ne $expectedRoles.Count) {
    throw "Expected $($expectedRoles.Count) reviewed Codex agent files; found $($agentFiles.Count)."
}

foreach ($role in $expectedRoles.Keys) {
    $agentPath = Join-Path $agentsRoot "$role.toml"
    $agentText = Get-RequiredText -Path $agentPath
    Assert-UniqueStringAssignment $agentText 'name' $role ".codex/agents/$role.toml"
    Assert-UniqueStringAssignment $agentText 'model' $allowedModel ".codex/agents/$role.toml"
    Assert-UniqueStringAssignment `
        $agentText `
        'model_reasoning_effort' `
        $expectedRoles[$role] `
        ".codex/agents/$role.toml"
}

$hooksText = Get-RequiredText -Path $hooksPath
try {
    $hooks = $hooksText | ConvertFrom-Json
}
catch {
    throw ".codex/hooks.json is not valid JSON: $($_.Exception.Message)"
}

if ($null -eq $hooks.hooks.SessionStart -or $null -eq $hooks.hooks.PreToolUse) {
    throw '.codex/hooks.json must register SessionStart and PreToolUse guards.'
}

[void] (Get-RequiredText -Path $hookScript)
$guideText = Get-RequiredText -Path $agentsGuide
if ($guideText -notmatch 'only allowed baseline and subagent model is \*\*`gpt-5\.6-sol`\*\*') {
    throw 'AGENTS.md does not contain the canonical Sol-only policy statement.'
}

Write-Host (
    "Codex agent config check passed: model=$allowedModel; " +
    'lead=medium; plan=xhigh; roles=low/medium/high/xhigh/max; max-subagents=2.')
