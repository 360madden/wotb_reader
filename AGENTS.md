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

These documents are authoritative over older handoffs or remembered status.

## Codex model and delegation policy

The only allowed baseline and subagent model is **`gpt-5.6-sol`**. Change the
reasoning effort, not the model. Do not select Terra, Luna, an external provider,
or another model in a spawn request, saved profile, skill, or repository
instruction. A different model requires a new explicit owner instruction and a
reviewed policy update before use.

| Agent | Effort | Use for |
|---|---:|---|
| lead / `default` / `worker` | `medium` | normal planning, implementation, integration |
| `explorer` | `low` | one read-only codebase question or evidence lookup |
| `verifier` | `low` | smallest sufficient focused check after a unit |
| `implementer_glue` | `medium` | bounded UI, DTO, HTTP, tests, or docs glue |
| `decoder_auditor` | `high` | replay, binary, decoder, or contract evidence audit |
| `security_auditor` | `xhigh` | loopback, mutation, privacy, ACL, or fail-closed audit |

Repository sources of truth are `.codex/config.toml`, `.codex/agents/*.toml`,
`.codex/hooks.json`, and `scripts/codex-agent-config-check.ps1`.

- Default to the lead agent only. Delegate only when the user asks, an
  applicable skill requires it, or at least two genuinely independent units
  justify their extra context cost.
- Do not delegate trivial answers, one-file mechanical edits, serial
  dependencies, or work whose coordination cost is likely higher than doing it
  once on the lead.
- Start with one specialist. At most two subagents may be open concurrently.
  Prefer read-heavy parallelism; keep overlapping writers sequential.
- Give each subagent one bounded question or disjoint file ownership. Use the
  smallest useful history fork and require a compact evidence/result summary,
  not raw logs or broad narration.
- Never override `model` or `model_reasoning_effort` in spawn calls. Role files
  own the reviewed effort. `max` and `ultra` are not repository defaults.
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
| Replay decode | `offline/replay-format.md`; `decoder_auditor` for audit | never execute pickle or ship dynamic decoder DLLs |
| Telemetry data flow | `offline/data-flow.md` | never mutate an immutable decode run |
| Offset or memory evidence | `offline/memory-offsets.md`, `offline/offset-discovery.md`, ledger | no promotion without the documented proof/approval gate |
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

- 2026-08-14 — Sol-only model policy, role effort matrix, hook enforcement,
  configuration gate, and fresh-session smoke verified. Full repository gate:
  1,206 tests passed, 7 local opt-in skips, 0 build warnings, 0 errors; 5 policy
  tests passed.
