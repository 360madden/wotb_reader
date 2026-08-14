#Requires -Version 5.1

[CmdletBinding()]
param(
    [AllowEmptyString()]
    [string] $InputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedModel = 'gpt-5.6-sol'
$allowedAgentTypes = @(
    'default',
    'worker',
    'explorer',
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
            hookEventName          = 'PreToolUse'
            permissionDecision     = 'deny'
            permissionDecisionReason = $Reason
        }
    } | ConvertTo-Json -Compress -Depth 4
}

if ($activeModel -ne $allowedModel) {
    $reason = "This repository requires $allowedModel; active model '$activeModel' is denied."
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
    [string] $requestedModelProperty.Value -ne $allowedModel) {
    Write-ToolDenial -Reason (
        "Subagent model override '$($requestedModelProperty.Value)' is denied; " +
        "use the role's $allowedModel configuration.")
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
