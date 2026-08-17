# Pester smoke tests for the repository's allowed-model Codex hook.
# Allowed models: gpt-5.6-sol (all lanes) and deepseek-v4-pro (bounded
# lanes only). Spawn-time model/reasoning overrides and unreviewed roles
# are always denied; role files own each lane's model.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$hook = Join-Path $here '..\.codex\hooks\enforce-allowed-models.ps1'

function Invoke-ModelHook {
    param([Parameter(Mandatory)][hashtable] $InputObject)

    $json = $InputObject | ConvertTo-Json -Compress -Depth 8
    $output = & $hook -InputJson $json
    if ($null -eq $output) {
        return $null
    }

    return ($output | Out-String).Trim() | ConvertFrom-Json
}

Describe 'Codex allowed-model policy' {
    It 'allows a Sol root session without adding context' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'SessionStart'
            model           = 'gpt-5.6-sol'
            source          = 'startup'
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows a deepseek-v4-pro root session without adding context' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'SessionStart'
            model           = 'deepseek-v4-pro'
            source          = 'startup'
        }

        $result | Should BeNullOrEmpty
    }

    It 'stops a root session on a model outside the reviewed set' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'SessionStart'
            model           = 'gpt-5.6-terra'
            source          = 'startup'
        }

        $result.continue | Should Be $false
        $result.stopReason | Should Match 'reviewed models'
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

    It 'allows a bounded-lane spawn from a deepseek-v4-pro lead session' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'deepseek-v4-pro'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'worker'; task_name = 'bounded_change' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows a glue spawn from a deepseek lead session without an override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'deepseek-v4-pro'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'implementer_glue'; task_name = 'dto_glue' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows the extra-high strategist role without a spawn override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'strategist'; task_name = 'roadmap' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows the high-effort correctness reviewer without a spawn override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'code_reviewer'; task_name = 'review_diff' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows the high-effort evidence analyst without a spawn override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'evidence_analyst'; task_name = 'adjudicate' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'allows the xhigh-effort memory researcher without a spawn override' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'memory_researcher'; task_name = 'root_anchor' }
        }

        $result | Should BeNullOrEmpty
    }

    It 'denies any subagent model override, even to a reviewed model' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'deepseek-v4-pro'
            tool_name       = 'spawn_agent'
            tool_input      = @{
                agent_type = 'worker'
                model      = 'gpt-5.6-sol'
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
                agent_type       = 'verifier'
                reasoning_effort = 'xhigh'
                task_name        = 'verify'
            }
        }

        $result.hookSpecificOutput.permissionDecision | Should Be 'deny'
        $result.hookSpecificOutput.permissionDecisionReason | Should Match 'reasoning override'
    }

    It 'denies an unreviewed role' {
        $result = Invoke-ModelHook @{
            hook_event_name = 'PreToolUse'
            model           = 'gpt-5.6-sol'
            tool_name       = 'spawn_agent'
            tool_input      = @{ agent_type = 'ad_hoc_genius'; task_name = 'guess' }
        }

        $result.hookSpecificOutput.permissionDecision | Should Be 'deny'
        $result.hookSpecificOutput.permissionDecisionReason | Should Match 'reviewed role matrix'
    }
}