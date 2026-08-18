"""offline_check.py — Validate internal links and cross-document consistency.

Checks that every markdown link in offline/*.md resolves to an existing file
relative to the pack, and that links in the live operations docs
(docs/operations/*.md and docs/operations/blockers/*.md) resolve too.
Handoffs are excluded: they are append-only historical records whose internal
links must not fail the gate if a referenced path later changes. Also checks
research/README.md (the pack's canonical research index). External URLs and
fragment-only anchors are skipped. Repo-root links (../docs/... etc.) are
resolved against the repository root.

Modes:
    python scripts/python/offline_check.py             link check only
    python scripts/python/offline_check.py --refresh   regenerate offline/file-tree.md
                                                       from `git ls-files`, then link check
    python scripts/python/offline_check.py --check-fresh
                                                       fail if offline/file-tree.md is
                                                       stale vs `git ls-files` (gate/CI
                                                       mode), then link check

Every mode also runs two consistency checks:

- Blocker-numbering contiguity: the union of `## BLK-XXXX` headers across
  `docs/operations/blocker-log.md` and `docs/operations/blockers/*.md` must be
  exactly `0001..N` with no gaps, no intra-file duplicates, and no
  out-of-range numbers. Companion deep-dives may repeat a main-log number
  (e.g. BLK-0007 in the command-execution-gate record); numbers introduced
  only in a deep-dive (e.g. BLK-0008-0011 in the replay-decoder record) must
  still make the union contiguous.
- Ledger consistency: every `## `OD-RECOVERY-XXX` result` section in
  `docs/operations/offset-discovery-ledger.md` must have a matching row in the
  Historical experiment index, and the decision register's next planned
  session must match the workflow's `Session ID`.

If both --refresh and --check-fresh are passed, --refresh wins (regenerate
first, then the stale check passes by construction).

Exit code: 0 = all checks pass, 1 = broken links, a stale file-tree snapshot,
a blocker-numbering / ledger-consistency problem, or RECOVERY doc path
references that do not resolve.
"""

from __future__ import annotations

import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
OFFLINE_DIR = REPO_ROOT / "offline"
FILE_TREE_PATH = OFFLINE_DIR / "file-tree.md"
OPERATIONS_DIR = REPO_ROOT / "docs" / "operations"
BLOCKER_LOG_PATH = OPERATIONS_DIR / "blocker-log.md"
BLOCKERS_DIR = OPERATIONS_DIR / "blockers"
LEDGER_PATH = OPERATIONS_DIR / "offset-discovery-ledger.md"
WORKFLOW_PATH = OPERATIONS_DIR / "offset-discovery-workflow.md"
RECOVERY_DIR = REPO_ROOT / "RECOVERY"
LOG_DIR = REPO_ROOT / ".build"
LOG_DIR.mkdir(parents=True, exist_ok=True)
LOG_PATH = LOG_DIR / f"offline-check-{datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')}.log"

MARKDOWN_LINK = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
FENCE = re.compile(r"^(```|~~~)")

# Link targets that are not file paths.
SKIP_PREFIXES = ("http://", "https://", "mailto:", "#")

# Header written by --refresh / verified by --check-fresh. Keep it in sync with
# the pack's maintenance rules (offline/README.md).
FILE_TREE_HEADER = """# Physical file tree snapshot

Auto-generated from `git ls-files` (the committed tree) by
`scripts/python/offline_check.py --refresh`. Regenerate whenever the layout
changes — the gate (`scripts/validate.ps1` and CI) fails if this snapshot is
stale.

This snapshot is the committed source of truth for path resolution. Note: two
stray `%~dp0.data/...` entries are committed (a past cmd-wrapper quoting bug) —
do not create new paths like that. Uncommitted work is absent here by design;
regenerate this file in the same change that adds, renames, or removes files.

## Files

```text
"""

FILE_TREE_FOOTER = "```\n"


def committed_paths() -> list[str]:
    """Return the sorted `git ls-files` listing (repo-root relative)."""
    result = subprocess.run(
        ["git", "ls-files"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=True,
    )
    return sorted(p for p in result.stdout.splitlines() if p)


def generate_file_tree(paths: list[str] | None = None) -> str:
    """Render the full file-tree.md content (header + body + footer)."""
    if paths is None:
        paths = committed_paths()
    return FILE_TREE_HEADER + "\n".join(paths) + "\n" + FILE_TREE_FOOTER


def _read_normalized() -> str:
    """Read file-tree.md with CRLF normalized to LF for stable comparisons."""
    return FILE_TREE_PATH.read_text(encoding="utf-8").replace("\r\n", "\n")


def refresh_file_tree() -> bool:
    """Regenerate offline/file-tree.md. Returns True if content changed."""
    content = generate_file_tree()
    if FILE_TREE_PATH.is_file() and _read_normalized() == content:
        print("file-tree.md already up to date.")
        return False
    FILE_TREE_PATH.write_text(content, encoding="utf-8", newline="\n")
    print(f"Regenerated {FILE_TREE_PATH.relative_to(REPO_ROOT).as_posix()}.")
    return True


def file_tree_is_fresh() -> tuple[bool, list[str], list[str]]:
    """Return (fresh, expected, actual) for the file-tree snapshot.

    Freshness is a full-content comparison (header + body), so a hand-edited
    header — which the script owns via FILE_TREE_HEADER — fails the check too.
    expected/actual are the path bodies, used for the diagnostics message.
    """
    expected = committed_paths()
    if not FILE_TREE_PATH.is_file():
        return False, expected, []
    content = _read_normalized()
    if content == generate_file_tree(expected):
        return True, expected, expected
    # Full content differs — parse the body only for the diagnostics message.
    lines = content.splitlines()
    try:
        start = lines.index("```text") + 1
        end = lines.index("```", start)
    except ValueError:
        return False, expected, []
    actual = [line for line in lines[start:end] if line]
    return False, expected, actual


BLK_HEADER = re.compile(r"^##\s+BLK-(\d{4})(?:\s|—|:|$)")


def collect_blk_numbers() -> list[tuple[str, int]]:
    """Return (repo-relative path, number) for every `## BLK-XXXX` header."""
    files = [BLOCKER_LOG_PATH, *sorted(BLOCKERS_DIR.glob("*.md"))]
    found: list[tuple[str, int]] = []
    for path in files:
        if not path.is_file():
            continue
        rel = path.relative_to(REPO_ROOT).as_posix()
        for line in path.read_text(encoding="utf-8").splitlines():
            match = BLK_HEADER.match(line.strip())
            if match:
                found.append((rel, int(match.group(1))))
    return found


def check_blocker_numbering() -> int:
    """Verify BLK-XXXX contiguity. Returns exit code (0 = clean)."""
    if not BLOCKER_LOG_PATH.is_file():
        print(f"ERROR: blocker register missing: {BLOCKER_LOG_PATH}")
        return 1

    entries = collect_blk_numbers()
    if not entries:
        print(f"ERROR: no BLK-XXXX headers found under {BLOCKER_LOG_PATH.parent}")
        return 1

    main_rel = BLOCKER_LOG_PATH.relative_to(REPO_ROOT).as_posix()
    by_file: dict[str, list[int]] = {}
    by_number: dict[int, list[str]] = {}
    for rel, number in entries:
        by_file.setdefault(rel, []).append(number)
        by_number.setdefault(number, []).append(rel)

    problems: list[str] = []
    for rel in sorted(by_file):
        numbers = by_file[rel]
        dupes = sorted({n for n in numbers if numbers.count(n) > 1})
        if dupes:
            problems.append(
                f"{rel}: duplicate BLK headers "
                + ", ".join(f"BLK-{n:04d}" for n in dupes)
            )

    # A number may appear in the main log plus one companion deep-dive, or in
    # the main log only, or in one deep-dive only. Two deep-dives sharing a
    # number (with no main-log owner) is an ambiguous duplicate.
    for number in sorted(by_number):
        files = by_number[number]
        deep_files = [f for f in files if f != main_rel]
        if len(deep_files) > 1:
            problems.append(
                f"BLK-{number:04d} appears in multiple deep-dives: "
                + ", ".join(files)
            )

    union = sorted({n for _, n in entries})
    maximum = union[-1]
    expected = set(range(1, maximum + 1))
    missing = sorted(expected - set(union))
    if missing:
        problems.append(
            "BLK numbering gap: missing "
            + ", ".join(f"BLK-{n:04d}" for n in missing)
        )

    if problems:
        print("ERROR: blocker numbering is inconsistent:")
        for problem in problems:
            print(f"  - {problem}")
        print(
            "Fix: keep BLK-XXXX contiguous across blocker-log.md and blockers/ "
            "(deep-dives may repeat a main-log number or introduce the next ones)."
        )
        return 1

    print(
        f"Blocker numbering OK: BLK-0001..BLK-{maximum:04d} contiguous "
        f"across {len(by_file)} record file(s)."
    )
    return 0


RESULT_HEADING = re.compile(r"^## `(OD-RECOVERY-[^`]+)` result\b", flags=re.M)
INDEX_ROW = re.compile(r"^\| `(OD-RECOVERY-[^`]+)` \|", flags=re.M)
PLANNED_ROW = re.compile(
    r"^\| Next planned session \| `(OD-RECOVERY-[^`]+)`", flags=re.M
)
SESSION_ID_LINE = re.compile(r"^\s*sessionId:\s*(OD-RECOVERY-[^\s]+)", flags=re.M)
SUPERSEDES_LINE = re.compile(r"^\s*supersedes:\s*(.*)$", flags=re.M)
SESSION_REF = re.compile(r"OD-RECOVERY-[A-Z0-9-]+")


def check_ledger_consistency() -> int:
    """Verify OD-RECOVERY session IDs are recorded consistently.

    Checks, all fail-closed:
    - every `## `OD-RECOVERY-XXX` result` section has a matching row in the
      Historical experiment index table;
    - the decision register's next planned session matches the workflow's
      `Session ID`;
    - every result section's YAML `sessionId` matches its heading;
    - every OD-RECOVERY reference in a `supersedes:` value resolves to a
      known session (an index row, a result section, or the planned session).
    Returns exit code (0 = clean).
    """
    if not LEDGER_PATH.is_file():
        print(f"ERROR: offset-discovery ledger missing: {LEDGER_PATH}")
        return 1

    text = LEDGER_PATH.read_text(encoding="utf-8")
    problems: list[str] = []

    result_ids = RESULT_HEADING.findall(text)
    index_ids = INDEX_ROW.findall(text)
    planned = PLANNED_ROW.search(text)
    planned_id = planned.group(1) if planned else None

    missing_from_index = sorted(set(result_ids) - set(index_ids))
    if missing_from_index:
        problems.append(
            "ledger result sections missing from Historical experiment index: "
            + ", ".join(missing_from_index)
        )

    if WORKFLOW_PATH.is_file():
        workflow = WORKFLOW_PATH.read_text(encoding="utf-8")
        workflow_session = re.search(r"Session ID: `(OD-RECOVERY-[^`]+)`", workflow)
        if planned_id and workflow_session and planned_id != workflow_session.group(1):
            problems.append(
                f"ledger next planned session {planned_id} != "
                f"workflow Session ID {workflow_session.group(1)}"
            )

    # Split the ledger on result headings so each section's YAML block is
    # inspected on its own.
    matches = list(RESULT_HEADING.finditer(text))
    for i, match in enumerate(matches):
        heading_id = match.group(1)
        block_end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        block = text[match.end():block_end]

        yaml_session = SESSION_ID_LINE.search(block)
        if yaml_session is None:
            problems.append(f"{heading_id}: result section has no `sessionId:` in its YAML block")
        elif yaml_session.group(1) != heading_id:
            problems.append(
                f"{heading_id}: sessionId {yaml_session.group(1)} != heading"
            )

        known_ids = set(index_ids) | set(result_ids)
        if planned_id:
            known_ids.add(planned_id)
        supersedes = SUPERSEDES_LINE.search(block)
        if supersedes:
            for ref in SESSION_REF.findall(supersedes.group(1)):
                if ref not in known_ids:
                    problems.append(f"{heading_id}: supersedes references unknown session {ref}")

    if problems:
        print("ERROR: offset-discovery ledger is inconsistent:")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    print(
        f"Ledger consistency OK: {len(set(result_ids))} result section(s), "
        f"{len(set(index_ids))} index row(s)."
    )
    return 0


def extract_links(md_file: Path) -> list[tuple[int, str]]:
    """Return (line_number, target) pairs for markdown links in a file.

    Links inside fenced code blocks are ignored: they are code, not
    navigation.
    """
    found: list[tuple[int, str]] = []
    in_fence = False
    for line_number, line in enumerate(
        md_file.read_text(encoding="utf-8").splitlines(), start=1
    ):
        if FENCE.match(line.strip()):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        for match in MARKDOWN_LINK.finditer(line):
            target = match.group(1).strip()
            # Strip an optional title suffix: [text](target "title").
            for quote in ('"', "'"):
                marker = f" {quote}"
                if marker in target and target.endswith(quote):
                    target = target.split(marker, 1)[0].rstrip()
                    break
            if any(target.startswith(prefix) for prefix in SKIP_PREFIXES):
                continue
            found.append((line_number, target))
    return found


def resolve_target(md_file: Path, target: str) -> Path | None:
    """Resolve a markdown link target to an existing path, or None."""
    # Fragment-only links point within the same file — always fine.
    if "#" in target:
        path_part = target.split("#", 1)[0]
        if not path_part:
            return md_file
        target = path_part

    candidate = (md_file.parent / target).resolve()
    return candidate if candidate.exists() else None


def check_links() -> int:
    """Link-check the pack. Returns exit code (0 = clean)."""
    if not OFFLINE_DIR.is_dir():
        print(f"ERROR: offline/ not found at {OFFLINE_DIR}")
        return 1

    md_files = sorted(OFFLINE_DIR.glob("*.md"))
    # The pack presents research/README.md as the canonical research index, so
    # its internal links are part of the discovery surface and get checked too.
    research_index = REPO_ROOT / "research" / "README.md"
    if research_index.is_file():
        md_files.append(research_index)
    # Live operations docs (blocker log, deep-dives, README index, guide,
    # ledger, workflow) are part of the navigation surface as well. Handoffs
    # are excluded: they are append-only historical records whose internal
    # links must not fail the gate if a referenced path later changes.
    md_files.extend(sorted(OPERATIONS_DIR.glob("*.md")))
    md_files.extend(sorted(BLOCKERS_DIR.glob("*.md")))
    md_files = sorted(set(md_files))
    broken: list[tuple[str, int, str]] = []
    total_links = 0

    with LOG_PATH.open("w", encoding="utf-8") as log:
        log.write(f"offline_check.py — {datetime.now(timezone.utc).isoformat()}\n")
        for md_file in md_files:
            links = extract_links(md_file)
            log_name = md_file.relative_to(REPO_ROOT).as_posix()
            log.write(f"\n{log_name}: {len(links)} links\n")
            for line_number, target in links:
                total_links += 1
                resolved = resolve_target(md_file, target)
                status = "ok" if resolved is not None else "BROKEN"
                log.write(f"  {status:6} line {line_number:3}: {target}\n")
                if resolved is None:
                    broken.append((log_name, line_number, target))

    print(f"Checked {len(md_files)} files, {total_links} links, {len(broken)} broken.")
    if broken:
        for name, line, target in broken:
            print(f"  {name}:{line} -> {target}")
        print(f"Details: {LOG_PATH}")
        return 1

    print(f"All links resolve. Log: {LOG_PATH}")
    return 0


# Backticked repo-relative paths in RECOVERY/*.md must resolve. The
# module's docs use code spans rather than markdown links, so the link
# checker above would miss a renamed/moved path; this closes that gap.
RECOVERY_PATH_SKIP_PREFIXES = (".build/", ".data/", ".freebuff/", "http:", "https:", "C:", "\\\\")
RECOVERY_PATH_PLACEHOLDERS = "<>*?$\""


def check_recovery_paths() -> int:
    """Verify backticked repo-relative paths in RECOVERY/*.md exist."""
    broken: list[tuple[str, int, str]] = []
    in_fence = False
    for md_file in sorted(RECOVERY_DIR.glob("*.md")):
        for line_number, line in enumerate(
            md_file.read_text(encoding="utf-8").splitlines(), start=1
        ):
            if FENCE.match(line.strip()):
                in_fence = not in_fence
                continue
            if in_fence:
                continue
            for token in re.findall(r"`([^`]+)`", line):
                token = token.strip()
                if not token or token.startswith(RECOVERY_PATH_SKIP_PREFIXES):
                    continue
                if any(ch in token for ch in RECOVERY_PATH_PLACEHOLDERS):
                    continue
                # Shell commands (contain spaces) and API endpoints
                # (root-absolute, e.g. /discover/...) are not repo paths.
                if " " in token or token.startswith("/"):
                    continue
                # Only path-like tokens are checked: markdown filenames, or
                # tokens that carry a path separator AND look like a path (an
                # extension on the basename, or a known repo-directory
                # prefix). Field names with slashes (playerPositionX/Y/Z),
                # bare .json/.ps1/.exe names, and shell commands are skipped.
                has_sep = "/" in token or "\\" in token
                basename = token.split("/")[-1].split("\\")[-1]
                known_dir_prefix = token.startswith(
                    (
                        "docs/", "scripts/", "tools/", "memory-offsets/",
                        "offline/", "RECOVERY/", "research/", "src/",
                        "tests/", ".codex/", ".opencode/", ".grok/",
                        ".agents/", ".github/",
                    )
                )
                if not (token.endswith(".md")
                        or (has_sep and ("." in basename or known_dir_prefix))):
                    continue
                candidates = (md_file.parent / token, REPO_ROOT / token)
                if not any(
                    candidate.is_file() or candidate.is_dir()
                    for candidate in candidates
                ):
                    broken.append(
                        (md_file.relative_to(REPO_ROOT).as_posix(), line_number, token)
                    )
    if broken:
        print("ERROR: RECOVERY docs reference missing paths:")
        for name, line, target in broken:
            print(f"  {name}:{line} -> `{target}`")
        return 1
    return 0


def main(argv: list[str]) -> int:
    mode = "check"
    if "--refresh" in argv:
        mode = "refresh"
    elif "--check-fresh" in argv:
        mode = "check-fresh"

    if mode == "refresh":
        refresh_file_tree()
    elif mode == "check-fresh":
        fresh, expected, actual = file_tree_is_fresh()
        if not fresh:
            missing = sorted(set(expected) - set(actual))
            extra = sorted(set(actual) - set(expected))
            print(
                "ERROR: offline/file-tree.md is stale (committed tree changed). "
                f"{len(missing)} missing, {len(extra)} extra entries."
            )
            if actual == expected:
                print("  The path body matches but the header/footer differs from the "
                      "script-owned FILE_TREE_HEADER/FOOTER — run --refresh to restore it.")
            elif not missing and not extra:
                print("  The path body has the same paths but reordered or duplicated — "
                      "run --refresh to re-sort it.")
            if missing:
                print("  missing: " + ", ".join(missing[:8]) + ("..." if len(missing) > 8 else ""))
            if extra:
                print("  extra:   " + ", ".join(extra[:8]) + ("..." if len(extra) > 8 else ""))
            print("Fix: run `python scripts/python/offline_check.py --refresh` and commit the result.")
            return 1
        print("file-tree.md is up to date.")

    link_exit = check_links()
    recovery_exit = check_recovery_paths()
    numbering_exit = check_blocker_numbering()
    ledger_exit = check_ledger_consistency()
    return link_exit or recovery_exit or numbering_exit or ledger_exit


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
