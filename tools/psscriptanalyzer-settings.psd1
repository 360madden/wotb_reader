# PSScriptAnalyzer settings profile for the WotB Treader repository.
#
# Loaded by scripts/invoke-scriptanalyzer.ps1. Custom rules live in
# tools/psscriptanalyzer-custom-rules.psm1 (passed via -CustomRulePath so the
# paths resolve relative to the repo root, not this file).
#
# IMPORTANT (verified against the 1.25.0 source, Engine/Settings.cs +
# Engine/Generic/ConfigurableRule.cs): the settings-file `Rules` hashtable
# severity promotion is SILENTLY IGNORED. ConfigurableRule only supports an
# `Enable` property; the cmdlet -Severity parameter is a rule FILTER (which
# rules run), not a severity reclassifier. The repo's fatal-rule list is
# therefore enforced in scripts/invoke-scriptanalyzer.ps1 by RULE NAME:
#   PSReviewUnusedParameter, PSAvoidAssignmentToAutomaticVariable,
#   PSUseCompatibleSyntax
# Any finding from those rules fails the gate even when PSSA labels it Warning.
# Those three rules are deliberately NOT excluded here: they stay active so
# the gate's by-name list decides their fate. Exclusions below apply only to
# rules whose naming/value is a documented operator-tooling convention.

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

        # Verb/noun naming exists for discoverable EXPORTED cmdlets. The OD
        # workflow scripts define internal helper functions (Quit-WatchOffline,
        # Ensure-Game, Get-Rendezvous -- a singular loanword -- and
        # Stop-OdProcesses, deliberately plural: it stops several). Renaming
        # them would churn call sites without adding discoverability value.
        'PSUseApprovedVerbs'
        'PSUseSingularNouns'
    )
}
