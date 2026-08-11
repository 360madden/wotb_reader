# Python Scaffolding Scripts

Utility scripts for development and testing of WotB Treader. All scripts use
stdlib only — no pip installs required.

## Setup

```powershell
cd C:\work\wotb_reader
python -m venv .venv
.venv\Scripts\activate
```

The venv is gitignored. Only stdlib needed for these scripts.

## Scripts

### `e2e_smoke.py` — End-to-End Smoke Test

Publishes the web host, starts it on a random port, hits every API endpoint,
and validates JSON response schemas. Reports pass/fail with timestamped logging.

```powershell
python scripts/python/e2e_smoke.py
```

**What it tests:**
- `dotnet publish` succeeds
- Host starts and responds to health checks
- `GET /api/v1/doctor` — all 5 checks pass
- `GET /api/v1/sessions` — pagination contract valid
- `GET /api/v1/game/state` — state fields present
- `GET /api/v1/maps/boundaries` — returns array
- Dashboard pages: `/`, `/comparisons`, `/diagnostics` — all HTTP 200

**Output:** `.build/e2e-smoke-<datetime>.log`

**Exit code:** 0 = all pass, 1 = any failure.

### `offline_check.py` — Offline Discovery Pack Link Checker

Validates every internal markdown link in `offline/*.md` (and
`research/README.md`, the pack's canonical research index) resolves to an
existing file. External URLs and fragment-only anchors are skipped.

```powershell
python scripts/python/offline_check.py              # link check only
python scripts/python/offline_check.py --refresh    # regenerate offline/file-tree.md, then link check
python scripts/python/offline_check.py --check-fresh  # fail if file-tree.md is stale, then link check
```

**What it checks:**
- All `[text](target)` links in every `offline/*.md` file (+ `research/README.md`)
- Relative paths resolved against the pack (and repo root via `../`)
- Broken links reported with file, line, and target
- `--check-fresh`: the committed `file-tree.md` body matches `git ls-files | sort`

**Output:** `.build/offline-check-<datetime>.log`

**Exit code:** 0 = all checks pass, 1 = broken links or a stale file-tree snapshot.

### `verify-camera-projection.py` — W2S Projection Validator (CAM-001 v7)

Validates the world-to-screen math end-to-end: projects the decoded
viewpoint tank through the LIVE memory camera (GameCamera pose from a
CAM-001 v7 aggregate) using the exact `WorldToScreen.Project` formula and
checks the third-person look-at property — the camera aims at the tank, so
the tank must land near viewport center across the 70–110° FOV band, with a
small look-at angle. Also reports the pitch diagnostic (memory pitch vs the
pitch required to aim at the tank) so a wrong pitch convention is visible.

```powershell
python scripts/python/verify-camera-projection.py            # newest .data aggregate
python scripts/python/verify-camera-projection.py path.json  # specific aggregate
python scripts/python/verify-camera-projection.py --self-test  # synthetic fixture, CI-safe
```

**Exit code:** 0 = verified, 1 = validation failed, 2 = evidence missing
(no evaluable rounds — the session never resolved the tank).

### `offset_check.py` — Offset File Validator

Validates all `memory-offsets/*.json` files for schema compliance, SHA256
formatting, offset coverage, and plausibility.

```powershell
python scripts/python/offset_check.py                 # standard validation
python scripts/python/offset_check.py --check-schema  # + cross-verify the pack doc
```

**What it checks:**
- `schemaVersion` is 1
- `executableSha256` is a valid 64-char hex string
- Filename matches `gameVersion`
- All 8 expected fields present, counts known vs unknown
- Offset values are plausible (not too small, not > 2GB)
- No unknown extra fields
- `confidence` is valid summary metadata (`none`/`low`/`medium`/`high` only); it never promotes a field. Per-field `fieldValidation.status` and its required evidence control runtime promotion.
- `discoveredAtUtc` is present

**`--check-schema` adds:**
- Parses the documented contract from `offline/memory-offsets.md` (offset field
  names from the example JSON, confidence levels from the table, required
  top-level fields from the prose)
- Cross-verifies pack doc ↔ `memory-offsets/schema.json` ↔ this validator's own
  constants — any drift (e.g. a field added in one place but not the other)
  is reported as a `CROSS-CHECK ISSUE`
- Verifies each version file's offsets keys and confidence value against the
  documented contract (`DOC-CHECK ISSUE` on missing/extra fields)

**Output:** `.build/offset-check-<datetime>.log`

**Exit code:** 0 = all valid, 1 = issues found.

## Conventions

- All scripts write timestamped logs to `.build/`
- `REPO_ROOT` resolved from `__file__` (works from any CWD)
- Stdlib only — zero dependencies beyond Python 3.10+
- `main()` with `if __name__ == "__main__":` guard
- `write_log()` appends to a timestamped log file AND prints to stdout
- Exit code 0 = success, 1 = failure
