# PSScriptAnalyzer custom rules for the WotB Treader repository.
#
# These rules encode the repo's hard-won Windows PowerShell 5.1 compatibility
# lessons (the OD workflow host is 5.1.26100) so they can never regress:
#
#   1. PSBanNetCoreOnlyStaticMembers -- [double]::IsFinite / [single]::IsFinite
#      (and [System.Double]::IsFinite, [System.Single]::IsFinite) do not exist on
#      .NET Framework (PS 5.1). This bug class killed roll-replay-time-increased.ps1
#      exact-mode validation (MethodNotFound -> FAILED_unexpected exit 5).
#
#   2. PSBanPowerShell7OnlyOperators -- `??`, `??=`, `&&`, `||` are PS7-only.
#      On a 5.1 host a script containing them fails to parse (so the parse-error
#      gate catches it there); under pwsh 7 they parse fine, so this rule keeps
#      the gate consistent across hosts.
#
#   3. PSBanUninitializedVariableReads -- a read of a variable never assigned
#      anywhere in the file is the StrictMode landmine class (CAM-001,
#      2026-08-11): under Set-StrictMode -Version Latest the read THROWS
#      ("variable cannot be retrieved"), silently $null otherwise. Emits only
#      from the file-root call (see the corrected contract note below); skips
#      files that execute [scriptblock]::Create (the e2e scratch harness
#      pattern - the assigned set is provably incomplete there).
#
# PSScriptAnalyzer 1.25 external-rule contract (verified against the 1.25 source
# Engine/ScriptAnalyzer.cs GetExternalRecord + live testing):
#   * AST rules MUST be typed with a CONCRETE node type; a parameter typed with
#     the abstract base [Ast] never matches any concrete node. Typing to a
#     narrower node type (e.g. [MemberExpressionAst]) silently misses subclass
#     nodes (InvokeMemberExpressionAst), which is how '[double]::IsFinite(1.0)'
#     actually parses.
#   * CORRECTED 2026-08-11 (live-verified): a ScriptBlockAst rule is called ONCE
#     PER ScriptBlockAst NODE -- the file root AND every nested body -- and the
#     findings are MERGED. A per-body walk cannot see file-wide assignments or
#     a function's inline param block (it is a sibling of the body), so any
#     rule whose semantics are file-scoped MUST walk up to the root
#     (while ($null -ne $root.Parent) { $root = $root.Parent }) and emit only
#     when the passed node IS the root; nested calls return @(). This is what
#     PSBanUninitializedVariableReads does. (PSBanNetCoreOnlyStaticMembers is
#     subtree-scoped, so duplicate invocations are harmless for it.)
#   * TokenKind for $x++ / ++$x is PostfixPlusPlus / PrefixPlusPlus (MinusMinus
#     for --); a scoped variable's VariablePath.UserPath carries the scope
#     qualifier (script:fail) on both the write target and the read.
#   * PSSA 1.25 does NOT honor '# PSScriptAnalyzer -Rule <name> -Suppress'
#     comments for custom-rule findings (tested 2026-08-11) - do not rely on
#     comment suppression for these rules.
#   * The parameter NAME must end in "ast" (AST rules) or "token" (token rules);
#     discovery picks the FIRST matching parameter, so each rule exposes exactly
#     one. Token rules receive the full token array once per file.
#   * DiagnosticRecord must be built with the 7-arg constructor
#     (message, extent, ruleName, severity, null, null, null); the 4-arg
#     overload and Message/Extent property assignment no longer exist.
#   * ExternalRule.GetSeverity() is always Warning, so a cmdlet-level
#     -Severity Error filter silently drops custom rules; the gate must filter
#     RECORD severity after the run instead.

# Require the analyzer types. Custom rules run inside Invoke-ScriptAnalyzer,
# which already loads the ScriptAnalyzer assembly; this guard keeps the module
# importable in isolation for testing without hard-failing.
if (-not ('Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord' -as [type])) {
    throw 'PSScriptAnalyzer assembly is not loaded; custom rules must be invoked via Invoke-ScriptAnalyzer.'
}

function New-DiagnosticRecord {
    param(
        [string] $Message,
        [System.Management.Automation.Language.IScriptExtent] $Extent,
        [string] $RuleName
    )

    # The 1.25.0 DiagnosticRecord constructor is
    #   (message, extent, ruleName, severity, exceptionMessage, exceptionStackTrace, suggestedCorrections)
    # with the last three optional; Message/Extent/RuleName are read-only
    # properties, so the positional ctor is required.
    return [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord]::new(
        $Message,
        $Extent,
        $RuleName,
        [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticSeverity]::Error,
        $null, $null, $null)
}

function PSBanNetCoreOnlyStaticMembers {
    [CmdletBinding()]
    param(
        # Name ends in "ast" so discovery registers an AST rule; typed
        # ScriptBlockAst so it fires once on the file root (the proven match).
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.ScriptBlockAst] $ScriptBlockAst
    )

    $results = @()
    $hits = $ScriptBlockAst.FindAll({
            param($node)
            $member = $node -as [System.Management.Automation.Language.MemberExpressionAst]
            if ($null -eq $member) { return $false }
            if (-not $member.Static) { return $false }

            $typeExpr = $member.Expression -as [System.Management.Automation.Language.TypeExpressionAst]
            if ($null -eq $typeExpr) { return $false }

            $typeName = $typeExpr.TypeName.FullName
            if ($typeName -notin @('double', 'single', 'System.Double', 'System.Single')) { return $false }

            return $member.Member.Value -eq 'IsFinite'
        }, $true)

    foreach ($hit in $hits) {
        $message = "[double]::IsFinite / [single]::IsFinite do not exist on .NET Framework -- the OD workflow host runs Windows PowerShell 5.1. Use portable checks ([double]::IsNaN / PositiveInfinity / NegativeInfinity) instead."
        $results += New-DiagnosticRecord -Message $message -Extent $hit.Extent -RuleName $MyInvocation.MyCommand.Name
    }
    return $results
}

function PSBanUninitializedVariableReads {
    [CmdletBinding()]
    param(
        # Name ends in "ast" so discovery registers an AST rule; typed
        # ScriptBlockAst so it fires once on the file root (the proven match).
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.ScriptBlockAst] $ScriptBlockAst
    )

    # Reads of a variable that is never assigned anywhere in the file are the
    # StrictMode-landmine class (CAM-001, 2026-08-11): under
    # Set-StrictMode -Version Latest the read THROWS ("variable cannot be
    # retrieved"); without StrictMode it silently evaluates to $null. Either
    # way the variable can never hold intended data, so every occurrence is a
    # defect or dead code. Automatic variables and drive-qualified ($env:)
    # reads are exempt - they are bound by the engine, not the file.
    #
    # The analyzer calls a ScriptBlockAst rule ONCE PER ScriptBlockAst NODE
    # (verified 2026-08-11): the file root AND every nested body, merging the
    # findings. A nested-body call cannot see file-wide assignments or the
    # function's inline param block (it is a sibling of the body), so per-body
    # analysis manufactures false positives. This rule is whole-file by design:
    # walk up to the root and emit only from the root call; nested calls no-op.
    $root = $ScriptBlockAst
    while ($null -ne $root.Parent) { $root = $root.Parent }
    if ($root -ne $ScriptBlockAst) { return @() }
    $ScriptBlockAst = $root

    # Dynamic scriptblock execution ([scriptblock]::Create + dot-source /
    # Invoke-Command) assigns variables the analyzer cannot see, so the
    # whole-file assigned set is provably incomplete - skip such files (only
    # the e2e scratch harnesses use this; the shipped drivers never do).
    $dynamic = $ScriptBlockAst.FindAll({
            param($n)
            $member = $n -as [System.Management.Automation.Language.MemberExpressionAst]
            if ($null -eq $member -or -not $member.Static) { return $false }
            $typeExpr = $member.Expression -as [System.Management.Automation.Language.TypeExpressionAst]
            return ($null -ne $typeExpr -and
                $typeExpr.TypeName.FullName -eq 'scriptblock' -and
                $member.Member.Value -eq 'Create')
        }, $true)
    if ($dynamic.Count -gt 0) { return @() }

    $automatic = @(
        '$_', '$args', '$Error', '$ErrorActionPreference', '$ConfirmPreference',
        '$DebugPreference', '$event', '$EventArgs', '$EventSubscriber',
        '$ExecutionContext', '$false', '$foreach', '$HOME', '$Host', '$input',
        '$IsLinux', '$IsMacOS', '$IsWindows', '$LASTEXITCODE', '$Matches',
        '$MyInvocation', '$NestedPromptLevel', '$null', '$OFS', '$PID',
        '$PROFILE', '$ProgressPreference', '$PSBoundParameters', '$PSCmdlet',
        '$PSCommandPath', '$PSCulture', '$PSDebugContext',
        '$PSDefaultParameterValues', '$PSHOME', '$PSItem',
        '$PSModuleAutoLoadingPreference', '$PSModulePath', '$PSScriptRoot',
        '$PSSenderInfo', '$PSSessionApplicationName',
        '$PSSessionConfigurationName', '$PSSessionOption', '$PSUICulture',
        '$PSVersionTable', '$PWD', '$Sender', '$ShellId', '$StackTrace',
        '$switch', '$this', '$true', '$VerbosePreference', '$WarningPreference',
        '$WhatIfPreference', '$InformationPreference', '$MaximumHistoryCount',
        '$MaximumVariableCount', '$PSStyle'
    )

    $results = @()
    $assigned = New-Object 'System.Collections.Generic.HashSet[string]'

    # Assignment contexts: assignment statements, param declarations, foreach
    # iteration variables, unary ++/-- (which parse as UnaryExpressionAst, NOT
    # AssignmentStatementAst - verified 2026-08-11 via $script:fail++), and
    # common -OutVariable/-ErrorVariable bindings.
    foreach ($node in $ScriptBlockAst.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.AssignmentStatementAst] -or
            $n -is [System.Management.Automation.Language.ParameterAst] -or
            $n -is [System.Management.Automation.Language.ForEachStatementAst] -or
            $n -is [System.Management.Automation.Language.UnaryExpressionAst]
        }, $true)) {
        if ($node -is [System.Management.Automation.Language.AssignmentStatementAst]) {
            [void]$assigned.Add($node.Left.VariablePath.UserPath)
        }
        elseif ($node -is [System.Management.Automation.Language.ParameterAst]) {
            [void]$assigned.Add($node.Name.VariablePath.UserPath)
        }
        elseif ($node -is [System.Management.Automation.Language.UnaryExpressionAst]) {
            # TokenKind for $x++ / ++$x is PostfixPlusPlus / PrefixPlusPlus
            # (MinusMinus for --) - verified 2026-08-11. UserPath carries the
            # scope qualifier (script:fail), matching the read walk's UserPath.
            if (([string]$node.TokenKind) -match 'PlusPlus|MinusMinus') {
                [void]$assigned.Add($node.Child.VariablePath.UserPath)
            }
        }
        else {
            [void]$assigned.Add($node.Variable.VariablePath.UserPath)
        }
    }
    foreach ($cmd in $ScriptBlockAst.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.CommandAst]
        }, $true)) {
        $elements = @($cmd.CommandElements)
        for ($i = 0; $i -lt $elements.Count; $i++) {
            $el = $elements[$i]
            if ($el -is [System.Management.Automation.Language.CommandParameterAst] -and
                $el.ParameterName -in @('OutVariable', 'ErrorVariable', 'InformationVariable') -and
                $null -ne $el.Argument -and
                $el.Argument -is [System.Management.Automation.Language.VariableExpressionAst]) {
                [void]$assigned.Add($el.Argument.VariablePath.UserPath)
            }
            elseif ($el -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                [string]$el.Value -match '^-(OutVariable|ErrorVariable|InformationVariable)$' -and
                $i + 1 -lt $elements.Count -and
                $elements[$i + 1] -is [System.Management.Automation.Language.VariableExpressionAst]) {
                [void]$assigned.Add($elements[$i + 1].VariablePath.UserPath)
            }
        }
    }

    foreach ($node in $ScriptBlockAst.FindAll({
            param($n)
            $n -is [System.Management.Automation.Language.VariableExpressionAst]
        }, $true)) {
        $variable = [System.Management.Automation.Language.VariableExpressionAst]$node
        if ($variable.VariablePath.IsDriveQualified) { continue }
        $name = $variable.VariablePath.UserPath
        if ($assigned.Contains($name) -or $automatic -contains ('$' + $name)) { continue }
        $message = "Variable '$'$name is read but never assigned anywhere in this file - the StrictMode landmine class (a read throws under Set-StrictMode -Version Latest, silently evaluates to `$null otherwise). Assign it before use or remove the dead read."
        $results += New-DiagnosticRecord -Message $message -Extent $variable.Extent -RuleName $MyInvocation.MyCommand.Name
    }
    return $results
}

function PSBanPowerShell7OnlyOperators {
    [CmdletBinding()]
    param(
        # Name ends in "token" (NOT "tokens") so discovery registers a token
        # rule; receives the full token array once per file.
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.Language.Token[]] $Token
    )

    $results = @()
    foreach ($token in $Token) {
        if ($token.Text -notin @('??', '??=', '&&', '||')) { continue }
        $message = "'$($token.Text)' is a PowerShell 7-only operator -- the OD workflow host runs Windows PowerShell 5.1, where it is a parse error. Use 5.1-compatible syntax (e.g. if/else, -and/-or, explicit null checks)."
        $results += New-DiagnosticRecord -Message $message -Extent $token.Extent -RuleName $MyInvocation.MyCommand.Name
    }
    return $results
}

Export-ModuleMember -Function PSBanNetCoreOnlyStaticMembers, PSBanUninitializedVariableReads, PSBanPowerShell7OnlyOperators
