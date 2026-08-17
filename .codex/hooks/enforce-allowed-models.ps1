#Requires -Version 5.1

[CmdletBinding()]
param(
    [AllowEmptyString()]
    [string] $InputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The role files (.codex/agents/*.toml) own each lane's model:
#   gpt-5.6-sol        - all lanes (specialists: high/xhigh)
#   deepseek-v4-pro    - bounded lanes (lead/default/worker, explorer,
#                        verifier, implementer_glue)
# This hook only rejects sessions/spawns that try to sidestep the review:
# an out-of-set session model, any spawn-time model or reasoning override,
# or an unreviewed role.
$allowedModels = @(
    'gpt-5.6-sol',
    'deepseek-v4-pro'
)
$allowedAgentTypes = @(
    'default',
    'worker',
    'explorer',
    'code_reviewer',
    'evidence_analyst',
    'systems_analyst',
    'strategist',
    'memory_researcher',
    'decoder_auditor',
    'security_auditor',
    'implementer_glue',
    'verifier'
)

if (-not $PSBoundParameters.ContainsKey('InputJson')) {
    $InputJson = [Console]::In.ReadToEnd()
}

if ([string]::IsNullOrWhiteSpace($InputJson)) {
    throw 'Codex model-policy hook received no JSON input.'
}

$hookInput = $InputJson | ConvertFrom-Json
$eventName = [string] $hookInput.hook_event_name
$activeModel = [string] $hookInput.model

function Write-SessionDenial {
    param([Parameter(Mandatory)][string] $Reason)

    [ordered]@{
        continue      = $false
        stopReason    = $Reason
        systemMessage = $Reason
    } | ConvertTo-Json -Compress
}

function Write-ToolDenial {
    param([Parameter(Mandatory)][string] $Reason)

    [ordered]@{
        hookSpecificOutput = [ordered]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $Reason
        }
    } | ConvertTo-Json -Compress -Depth 4
}

$allowedModelText = ($allowedModels -join ' or ')
if ($activeModel -notin $allowedModels) {
    $reason = "This repository requires one of the reviewed models ($allowedModelText); active model '$activeModel' is denied."
    if ($eventName -eq 'SessionStart') {
        Write-SessionDenial -Reason $reason
        return
    }

    if ($eventName -eq 'PreToolUse') {
        Write-ToolDenial -Reason $reason
        return
    }
}

if ($eventName -ne 'PreToolUse') {
    return
}

$toolInput = $hookInput.tool_input
if ($null -eq $toolInput) {
    Write-ToolDenial -Reason 'Subagent spawn input is missing.'
    return
}

$requestedModelProperty = $toolInput.PSObject.Properties['model']
if ($null -ne $requestedModelProperty -and
    $null -ne $requestedModelProperty.Value -and
    -not [string]::IsNullOrWhiteSpace([string] $requestedModelProperty.Value)) {
    Write-ToolDenial -Reason (
        "Subagent model override '$($requestedModelProperty.Value)' is denied; " +
        'use the reviewed role configuration.')
    return
}

$reasoningProperty = $toolInput.PSObject.Properties['reasoning_effort']
if ($null -ne $reasoningProperty -and
    $null -ne $reasoningProperty.Value -and
    -not [string]::IsNullOrWhiteSpace([string] $reasoningProperty.Value)) {
    Write-ToolDenial -Reason (
        "Subagent reasoning override '$($reasoningProperty.Value)' is denied; " +
        'use the repository role matrix.')
    return
}

$agentTypeProperty = $toolInput.PSObject.Properties['agent_type']
if ($null -ne $agentTypeProperty -and
    $null -ne $agentTypeProperty.Value -and
    -not [string]::IsNullOrWhiteSpace([string] $agentTypeProperty.Value) -and
    [string] $agentTypeProperty.Value -notin $allowedAgentTypes) {
    Write-ToolDenial -Reason (
        "Subagent type '$($agentTypeProperty.Value)' is not in the reviewed role matrix.")
}