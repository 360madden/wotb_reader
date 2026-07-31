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

Validates every internal markdown link in `offline/*.md` resolves to an
existing file. External URLs and fragment-only anchors are skipped.

```powershell
python scripts/python/offline_check.py
```

**What it checks:**
- All `[text](target)` links in every `offline/*.md` file
- Relative paths resolved against the pack (and repo root via `../`)
- Broken links reported with file, line, and target

**Output:** `.build/offline-check-<datetime>.log`

**Exit code:** 0 = all links resolve, 1 = one or more broken.

### `offset_check.py` — Offset File Validator

Validates all `memory-offsets/*.json` files for schema compliance, SHA256
formatting, offset coverage, and plausibility.

```powershell
python scripts/python/offset_check.py
```

**What it checks:**
- `schemaVersion` is 1
- `executableSha256` is a valid 64-char hex string
- Filename matches `gameVersion`
- All 8 expected fields present, counts known vs unknown
- Offset values are plausible (not too small, not > 2GB)
- No unknown extra fields
- `confidence` is a valid value
- `discoveredAtUtc` is present

**Output:** `.build/offset-check-<datetime>.log`

**Exit code:** 0 = all valid, 1 = issues found.

## Conventions

- All scripts write timestamped logs to `.build/`
- `REPO_ROOT` resolved from `__file__` (works from any CWD)
- Stdlib only — zero dependencies beyond Python 3.10+
- `main()` with `if __name__ == "__main__":` guard
- `write_log()` appends to a timestamped log file AND prints to stdout
- Exit code 0 = success, 1 = failure
