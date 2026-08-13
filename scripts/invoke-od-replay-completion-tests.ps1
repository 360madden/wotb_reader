# Gate entry point for the completion-marker Pester smoke tests. Invoked by
# validate.ps1 via Invoke-CheckedNative; exits non-zero when any test fails so
# the gate fails closed. Pester 3.x does not throw on failed assertions -- it
# reports them in the result object -- so FailedCount drives the exit code.

$result = Invoke-Pester -Script (Join-Path $PSScriptRoot 'od-replay-completion.Tests.ps1') -PassThru

if ($null -eq $result -or $result.FailedCount -gt 0) {
    exit 1
}

exit 0
