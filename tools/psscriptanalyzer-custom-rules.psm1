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
# PSScriptAnalyzer 1.25 external-rule contract (verified against the 1.25 source
# Engine/ScriptAnalyzer.cs GetExternalRecord + live testing):
#   * AST rules MUST be typed with a CONCRETE node type; a parameter typed with
#     the abstract base [Ast] never matches any concrete node. Rules fire once
#     per matching node -- the file root ScriptBlockAst is the reliable match,
#     so rules typed [ScriptBlockAst] fire once per file and walk internally.
#     Typing to a narrower node type (e.g. [MemberExpressionAst]) silently
#     misses subclass nodes (InvokeMemberExpressionAst), which is how
#     '[double]::IsFinite(1.0)' actually parses.
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

Export-ModuleMember -Function PSBanNetCoreOnlyStaticMembers, PSBanPowerShell7OnlyOperators
