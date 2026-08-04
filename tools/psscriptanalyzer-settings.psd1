# PSScriptAnalyzer settings profile for the WotB Treader repository.
#
# Loaded by scripts/invoke-scriptanalyzer.ps1. Custom rules live in
# tools/psscriptanalyzer-custom-rules.psm1 (passed via -CustomRulePath so the
# paths resolve relative to the repo root, not this file).
#
# Severity mapping: the validate gate fails on Error + ParseError only.
# Warnings are reported in the JSON report but are not fatal, so the profile
# promotes the highest-value hygiene rules to Error and excludes only rules
# that would flag deliberate operator-tooling conventions.

@{
    ExcludeRules = @(
        # Scripts are operator-driven state changers by design (they launch,
        # suspend, click, and stop the game). ShouldProcess is a library
        # convention, not an interactive-tool one.
        'PSUseShouldProcessForStateChangingFunctions'

        # Write-Host is the deliberate operator-output convention across the
        # OD workflow scripts (they run in a dedicated window; the host has no
        # pipeline consumer).
        'PSAvoidUsingWriteHost'

        # Block comment help is not enforced on single-purpose automation
        # scripts.
        'PSProvideCommentHelp'
    )

    Rules = @{
        # Automatic variables are reserved ($Pid, $Host, ...). The OD workflow
        # once shipped a `param($Pid)` that silently never bound; this rule
        # makes that class a hard gate failure.
        'PSAvoidAssignmentToAutomaticVariable' = @{ Severity = 'Error' }

        # Unused parameters are almost always a typo or a wiring bug in this
        # repo's launch drivers (every parameter has a caller).
        'PSReviewUnusedParameter' = @{ Severity = 'Error' }

        # Syntax that parses on the current host but not on Windows PowerShell
        # 5.1 (the OD workflow host) is a hard failure.
        'PSUseCompatibleSyntax' = @{ Severity = 'Error' }

        # Assigned-but-never-used variables flag a typo'd read or dead code.
        'PSUseDeclaredVarsMoreThanAssignments' = @{ Severity = 'Warning' }
    }
}
