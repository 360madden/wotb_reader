# WotB Treader — agent entry

Windows-first .NET 10 modular monolith for **offline** WoTB replay telemetry.
No runtime AI, cloud, Python, Node.js, Rust, Electron, or containers. Development
scripts may use PowerShell and Python; shipped application code stays C#/.NET.

## Authority and current state

Do not copy changing project status into this always-loaded file. At the start of
work, read only the sources relevant to the request:

1. Newest file in `docs/operations/handoffs/`.
2. `docs/operations/product-roadmap.md` and
   `docs/operations/next-10-actions.md` for active product work.
3. For offset or live-memory work, the *Next planned session* row in
   `docs/operations/offset-discovery-ledger.md`, plus
   `docs/operations/blocker-log.md` and
   `docs/operations/offset-promotion-checklist.md` as needed.
4. `offline/README.md` for the focused repository index.
5. For build-drift work (a new game build, `decode_build_mismatch`, or a
   live lane that refuses reads), read `RECOVERY/` first — the triage script
   and the evidence-first re-verification playbook.

These documents are authoritative over older handoffs or remembered status.

## Codex model and delegation policy

The allowed baseline and subagent models are **`gpt-5.6-sol`** (all lanes)
and **`deepseek-v4-pro`** (bounded lanes only: lead `default`/`worker`,
`explorer`, `verifier`, `implementer_glue` — effort `medium` or less). The
specialist lanes (`code_reviewer`, `evidence_analyst`, `systems_analyst`,
`decoder_auditor`, `strategist`, `security_auditor`, `memory_researcher`)
stay `gpt-5.6-sol` only. A role file owns its model; change reasoning effort,
not a role's model, and never select Terra, Luna, an external provider, or a
model outside the two-model set in a spawn request, saved profile, skill, or
repository instruction. Changing the allowed set requires a new explicit owner
instruction and a reviewed policy update before use.

| Agent | Model | Effort | Sandbox | Use for |
|---|---|---|---|---|
| lead / `default` / `worker` | `deepseek-v4-pro` | `medium` | `workspace-write` | routing, bounded implementation, integration of frozen decisions |
| `explorer` | `deepseek-v4-pro` | `low` | `read-only` | one codebase question or evidence lookup |
| `verifier` | `deepseek-v4-pro` | `low` | `workspace-write` | smallest focused check; writes ignored build outputs only |
| `implementer_glue` | `deepseek-v4-pro` | `medium` | `workspace-write` | bounded UI, DTO, HTTP, tests, or docs glue |
| `code_reviewer` | `gpt-5.6-sol` | `high` | `read-only` | correctness review of a diff, branch, or defined scope |
| `evidence_analyst` | `gpt-5.6-sol` | `high` | `read-only` | clock/scorer/experiment and cross-replay evidence adjudication |
| `systems_analyst` | `gpt-5.6-sol` | `high` | `read-only` | difficult cross-project trace or multi-file root cause |
| `decoder_auditor` | `gpt-5.6-sol` | `high` | `read-only` | replay, binary, decoder, or contract evidence audit |
| `strategist` | `gpt-5.6-sol` | `xhigh` | `read-only` | long-range product, architecture, or experiment-campaign planning |
| `security_auditor` | `gpt-5.6-sol` | `xhigh` | `read-only` | loopback, mutation, privacy, ACL, or fail-closed audit |
| `memory_researcher` | `gpt-5.6-sol` | `xhigh` | `read-only` | unknown offsets/root anchors or failed/conflicting reverse-engineering hypotheses |

Repository sources of truth are `.codex/config.toml`, `.codex/agents/*.toml`,
`.codex/hooks.json`, and `scripts/codex-agent-config-check.ps1`.
The stable role boundaries and examples are in
`docs/operations/codex-agent-roster.md`; keep detailed guidance there instead
of growing this always-loaded file.

- Route by uncertainty and consequence, not file type. A known offset-chain
  implementation is medium; proving a known chain is high; discovering an
  unknown root anchor is xhigh.
- Use low for deterministic lookups/checks, medium for frozen-design work,
  high for multi-step causal analysis, xhigh for consequential planning or
  security decisions with competing tradeoffs, and the highest effort (xhigh)
  for the hardest quality-first single investigation.
- Long-range planning must use Plan mode (`xhigh`) or `strategist`. Unknown
  memory offsets, ownership roots, vtable/AOB anchors, and a reasoning-limited
  failed xhigh investigation must use `memory_researcher`.
- Do not escalate effort for missing access, absent live evidence, or a serial
  dependency; more reasoning cannot remove an external blocker. After a failed
  high/xhigh attempt, escalate only when competing hypotheses or reasoning
  quality—not missing evidence—caused the failure.
- Default to the lead agent for bounded work. Delegate one specialist whenever
  a task matches a high/xhigh lane that the medium lead should not absorb.
  Use multiple agents only for genuinely independent workstreams.
- Choose exactly one primary specialist for one question. Do not stack roles
  merely because a task touches several modules; add a second role only for a
  separate review dimension or an independent workstream.
- Do not delegate trivial answers, one-file mechanical edits, serial
  dependencies, or work whose coordination cost is likely higher than doing it
  once on the lead.
- Start with one specialist. The project session cap is six concurrent agent
  threads; one specialist remains the normal case. Prefer read-heavy parallelism
  and keep overlapping writers sequential.
- Give each subagent one bounded question or disjoint file ownership. Use the
  smallest useful history fork and require a compact evidence/result summary,
  not raw logs or broad narration.
- Never override `model` or `model_reasoning_effort` in spawn calls. Role files
  own the reviewed effort. The highest effort tier on the schema (xhigh)
  belongs only to `memory_researcher`. Ultra is not a saved role: use it only
  when the owner explicitly requests a multi-agent campaign and at least two
  deep workstreams are independent.
- Subagents never stage, commit, or push. The lead owns integration and Git.
- The lead retains shared-contract decisions. Use the named specialist for
  decoder/binary or security/privacy audits when that audit is required.

## Session ritual

**Start**

1. Read the newest handoff and only the task-specific sources listed above.
2. Run `git status` and note the branch. Preserve unrelated or user-owned
   changes; never discard, overwrite, stash, or commit them.
3. Inspect recent commits when reviewing or continuing an existing workstream.

**End**

1. Verify in proportion to risk; milestone work runs `scripts/validate.ps1`.
2. Append a handoff and ledger/blocker entry when the workstream requires it.
   Handoffs and blocker records are immutable UTC history.
3. Refresh `docs/operations/next-10-actions.md` for milestone work. Re-anchor it
   to the roadmap and surface the offline-eligible sequence.
4. Report changes, verification, remaining work, and the clean/known-dirty tree.

## Hard constraints

- Treat the game install as read-only. Never modify or redistribute the install
  or game-derived assets.
- Offline discovery must use fresh managed lifecycle evidence:
  `verificationState=OfflineReplayVerified` and
  `reasonCode=session.offline_replay_verified`. A PID or assertion is not
  authorization.
- Never log or commit raw replay bytes, tokens, full private paths, account IDs,
  chat, screenshots, private captures, databases, or game assets. CI fixtures
  are synthetic only; private-game tests are local opt-in.
- Evidence first: unknown stays unknown. Reprocessing creates a new immutable
  decode run. Pickle is data only; never execute opcodes or import Python.
- Preserve architecture: `Core` has no project references; `Application`
  references `Core` only; adapters reference `Application` plus `Core`, never
  each other; `Bootstrap` is the sole composition root.
- The WPF overlay is a transparent, borderless, topmost HUD and loopback web
  client. It has no parser or storage references and is not a session viewer.
- Only `Overlay` and `tools/GameHarness` target `net10.0-windows`; other
  projects remain portable `net10.0`. Add new DI ports to the published-port
  list in `CompositionRootTests`.
- Propose shared-contract changes before editing them. Keep diffs focused; use
  no destructive Git and store no secrets.
- Commits use `Codex Agent <codex@local.invalid>` unless the owner says
  otherwise. Push only when asked and never force-push.

Bot status may be inferred from a name; player names and bot status are public
Wargaming statistics. Do not apply the private-data boundary to those facts.

## Task routing

| Task | Load / route | Stop condition |
|---|---|---|
| Design interview explicitly requested | `.agents/skills/grill-me/SKILL.md` | no implementation before shared understanding |
| Long-range roadmap, architecture, or experiment campaign | `strategist` (`xhigh`) plus current handoff/roadmap/blockers | strategist does not implement; lead owns the decision |
| Difficult cross-project causal trace | `systems_analyst` (`high`) | analyst proves the path and rejected hypotheses before implementation |
| Local diff, branch, or bounded correctness review | `code_reviewer` (`high`) | reviewer reports concrete findings and does not edit |
| Live/decoded experiment, scorer, clock alignment, or cross-replay claim | `evidence_analyst` (`high`) | immutable artifacts only; no artifact mutation, live action, or promotion |
| Replay decode | `offline/replay-format.md`; `decoder_auditor` for audit | never execute pickle or ship dynamic decoder DLLs |
| Telemetry data flow | `offline/data-flow.md` | never mutate an immutable decode run |
| Unknown offset, root anchor, pointer ownership, vtable/AOB, or conflicting memory evidence | `memory_researcher` (`xhigh`) plus memory docs/ledger | no live action or promotion; return a bounded proof protocol to the lead |
| Known-chain offset evidence or publication review | `systems_analyst` or `decoder_auditor` (`high`) | no promotion without documented proof and owner approval |
| Game internals research | `research/README.md` | never touch the game install |
| Architecture | `docs/architecture/overview.md`, architecture tests | preserve the enforced reference graph |
| Loopback, mutation, privacy, ACL | read-only `security_auditor` | shared-contract changes require lead review |
| UI, DTO, HTTP, tests, docs glue | `implementer_glue` when delegation is justified | no unreviewed shared-contract drift |
| Focused verification | `verifier` when delegation is justified | verifier does not edit, stage, or commit |
| Human setup | `README.md` | — |

## Commands and definition of done

| Use | Command |
|---|---|
| Restore | `dotnet restore WotBTreader.sln --locked-mode` |
| Build | `dotnet build WotBTreader.sln -c Release` |
| One test project | `dotnet test tests/WotBTreader.Core.Tests -c Release` |
| Focused test | add `--filter "FullyQualifiedName~SomeTest"` |
| Full milestone gate | `scripts/validate.ps1` |

SDK and package versions are pinned. Warnings are errors, and vulnerable
transitive packages require a central pin rather than suppression. The full
gate performs locked restore, formatting, Release build/tests, repository and
privacy scan, Codex agent-policy checks, PowerShell analysis, offline-pack
freshness, and offset schema/chains validation. Add `-AuditPackages` when the
transitive vulnerability audit is required.

Done means focused checks pass, required documentation is appended, milestone
work passes the full gate, and no stray files remain. Before committing, review
`git diff` and recent message style, stage only related paths, and use a
conventional commit. Never use broad `git add -A` in a dirty tree.

When files are added, renamed, or removed, stage the intended new paths before
running `python scripts/python/offline_check.py --refresh`; that command reads
`git ls-files`. PowerShell scripts must remain Windows PowerShell 5.1-compatible
and ASCII-only. See `docs/operations/cmd-wrapper-gotchas.md` for batch/cmd work.

## Last verified

- 2026-08-17 — owner-approved policy amendment: the allowed model set is now
  `gpt-5.6-sol` (all lanes) plus `deepseek-v4-pro` (bounded lanes:
  lead/default/worker, explorer, verifier, implementer glue). Enforcement
  extended: `.codex/hooks/enforce-allowed-models.ps1`, role toml pins,
  `scripts/codex-agent-config-check.ps1`, and the updated Pester policy tests
  re-run green.
- 2026-08-14 — complete 12-role Sol-only roster and effort ladder verified (at
  the time): Plan/strategy and security xhigh; correctness/evidence/systems/
  decoder high; unknown offset/root-anchor research max; bounded work medium;
  lookup/verification low. Every role has a checked read-only or
  workspace-write sandbox. Full repository gate: 1,206 tests passed, 7 local
  opt-in skips, 0 build warnings, 0 errors; 10 policy tests passed.
