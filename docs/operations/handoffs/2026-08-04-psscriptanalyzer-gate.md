# Handoff — PSScriptAnalyzer gate + PS 5.1 encoding hygiene (2026-08-04)

## Sessions worked

- Installed PSScriptAnalyzer 1.25.0 as a pinned, hash-verified external tool
  under the repo's `tools.lock.json` policy; verified it imports and runs on
  **both** Windows PowerShell 5.1 (the OD workflow host) and pwsh 7.
- Debugged from first principles why custom script rules are *discovered but
  never invoked* on 1.25 (root cause below, proven from source + empirics).
- Built the gate (`scripts/invoke-scriptanalyzer.ps1`) with a self-test, wired
  it into `scripts/validate.ps1` and CI, and used it to fix a real latent bug
  class: **non-ASCII bytes in BOM-less .ps1 files read as ANSI by 5.1**.

## What shipped

| Artifact | Purpose | Status |
|---|---|---|
| `tools/external/tools.lock.json` | PSScriptAnalyzer 1.25.0 registry entry (version, SHA-256, license, install notes) | New entry, minimal-diff |
| `scripts/install-psscriptanalyzer.ps1` | Pinned download → SHA-256 verify → extract to `tools/external/installed/` → both-host import smoke → registry verification patch | Idempotent, run-verified |
| `scripts/invoke-scriptanalyzer.ps1` | Gate: tracked `.ps1` scope, settings + custom rules, JSON report, exit 0/1/2/3, `-SelfTest` | Passing on 5.1 + pwsh 7 |
| `tools/psscriptanalyzer-settings.psd1` | Gate profile: Error+ParseError fail; promoted `PSAvoidAssignmentToAutomaticVariable` (the `$Pid` class), `PSReviewUnusedParameter`, `PSUseCompatibleSyntax` | New |
| `tools/psscriptanalyzer-custom-rules.psm1` | `PSBanNetCoreOnlyStaticMembers` (`[double]::IsFinite` class) + `PSBanPowerShell7OnlyOperators` (`??`/`??=`/`&&`/`||`) | New, self-test proven |
| `scripts/validate.ps1` + `.github/workflows/ci.yml` | Gate wired as a hard step (install → invoke) | Done |
| `tools/external/README.md` | Policy note: scripts must pass the gate before landing | Updated |
| `tools/compute-exe-hash.ps1`, `scripts/click-watch-offline.ps1`, `scripts/play-replay-from-hangar.ps1`, `scripts/launch-offline-replay-for-od.ps1`, `scripts/click-hangar-replay.ps1` | Non-ASCII → ASCII (5.1 ANSI-read safety) | Fixed |
| `knowledge.md`, `AGENTS.md` | Durable gotchas so future sessions don't re-learn the trap | Updated |

## Root cause: custom rules discovered but never invoked

PSScriptAnalyzer matches script rules to AST nodes by checking whether the
node's concrete type name is a **substring** of the rule parameter's type name
(`GetExternalRecord` in the 1.25 source). Consequences, all proven empirically:

1. A rule typed with the abstract `[Ast]` matches **no** concrete node and is
   silently never invoked. Rules must use a concrete node type (we use
   `[ScriptBlockAst]`, which fires once per file root — the same shape as the
   module's own sample rule).
2. `[double]::IsFinite(1.0)` parses as **`InvokeMemberExpressionAst`** (method
   call with args), not `MemberExpressionAst` — our first narrow typing was
   doubly wrong.
3. Discovery takes the first parameter whose name ends in `ast`/`token`; a
   parameter named `$Tokens` (plural) is silently dropped.
4. `-CustomRulePath` **replaces** the default rule set unless
   `-IncludeDefaultRules` is passed; and 1.25 declares external rules at
   Warning *rule-level* (`ExternalRule.GetSeverity`), so `-Severity Error` in
   the invocation silently drops them — filter records, not rules.
5. A 1.25 quirk: the module **hangs** when loaded via inline
   `powershell -Command "Import-Module ..."` but imports in 0.1s when loaded
   through a script file (`-File`). The install/invoke scripts always run as
   files; documented in the registry entry.

## PS 5.1 encoding trap (the gate's first real catch)

5.1 reads BOM-less UTF-8 files as ANSI (cp1252). An em-dash (`U+2014`, bytes
`E2 80 94`) has trailing byte `0x94` = `"` in cp1252, so a string literal
containing it terminates early → parse error. `tools/compute-exe-hash.ps1`
line 158 was exactly that ("Hash is empty — compute …"). Four more scripts had
non-ASCII in comments (em-dashes, curly quotes, `€`, `‰`, `→`, `…`) that
survived parsing but would mojibake at runtime under the 5.1 host. Repo
convention is BOM-less files, so the fix is ASCII-only source (no BOM added):
all five scripts are now ASCII-clean, verified by the gate.

## Validation

- `powershell -File scripts/invoke-scriptanalyzer.ps1 -SelfTest`: PASS (both
  5.1 and pwsh 7).
- Full gate: 16 tracked .ps1 analyzed, **0 Error/ParseError**, 30 warnings
  (mostly Information/Warning hygiene; reviewed, not gate-failing).
- Installer re-run 3×: idempotent, both-host smoke pass.
- `offline_check.py --check-fresh` (below) and tool-registry JSON validation
  pass.

## Next steps (ranked)

1. Decide whether to harden the 30 warnings (e.g. `PSUseSingularNouns`,
   `PSAvoidUsingPositionalParameters`) or keep them as advisory; the gate
   already blocks Error/ParseError.
2. Consider promoting `PSReviewUnusedParameter` hits to Error as usage grows.
3. If the custom-rules module grows, add more repo-specific bans (e.g. bare
   `Write-Host` in library scripts) — always `[ScriptBlockAst]`-typed, and
   re-run `-SelfTest`.

## Open items

- Gate requires network only at *install* time (pinned download); CI installs
  fresh each run. No offline-cached module yet — acceptable, CI has network.
- `offline/file-tree.md` must be refreshed if this changes the file tree (it
  does: 2 new scripts + 2 new tools files), then `offline_check.py --refresh`.
