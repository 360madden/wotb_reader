# Pester smoke tests for the repository's Sol-only Codex hook.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$hook = Join-Path $here '..\.codex\hooks\enforce-sol-model.ps1'

function Invoke-ModelHook {
    param([Parameter(Mandatory)][hashtable] $InputObject)

    $json = $InputObject | ConvertTo-Json -Compress -Depth 8
    $output = & $hook -InputJson $json
    if ($null -eq $output) {
        return $null
    }

    return ($output | Out-String).Trim() | ConvertFrom-Json
}

Describe 'Codex Sol-only model policy' {
    It 'allows a Sol root session without adding context' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'SessionStart'
            model           = 'gpt-5.6-sol'
            source          = 'startup'
        }

        $result | Should BeNullOrEmpty
    }

    It 'stops a root session on another model' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'SessionStart'
            model           = 'gpt-5.6-terra'
            source          = 'startup'
        }

        $result.continue | Should Be $false
        $result.stopReason | Should Match 'requires gpt-5.6-sol'
    }

    It 'allows a spawn that inherits the reviewed role configuration' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'explorer'; task_name = 'map_path' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'denies a non-Sol subagent model override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{
                agent_type = 'worker'
                model      = 'gpt-5.6-terra'
                task_name  = 'change'
            }
        }

        $result.hookSpecificOutput.permissionDecision | Should Be 'deny'
        $result.hookSpecificOutput.permissionDecisionReason | Should Match 'model override'
    }

    It 'denies a spawn-time reasoning override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{
                agent_type      = 'verifier'
                reasoning_effort = 'xhigh'
                task_name       = 'verify'
            }
        }

        $result.hookSpecificOutput.permissionDecision | Should Be 'deny'
        $result.hookSpecificOutput.permissionDecisionReason | Should Match 'reasoning override'
    }
}
