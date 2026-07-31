"""offline_check.py — Validate internal links in the offline/ discovery pack.

Checks that every markdown link in offline/*.md resolves to an existing file
relative to the pack. Also checks research/README.md (the pack's canonical
research index). External URLs and fragment-only anchors are skipped.
Repo-root links (../docs/... etc.) are resolved against the repository root.

Modes:
    python scripts/python/offline_check.py             link check only
    python scripts/python/offline_check.py --refresh   regenerate offline/file-tree.md
                                                       from `git ls-files`, then link check
    python scripts/python/offline_check.py --check-fresh
                                                       fail if offline/file-tree.md is
                                                       stale vs `git ls-files` (gate/CI
                                                       mode), then link check

If both --refresh and --check-fresh are passed, --refresh wins (regenerate
first, then the stale check passes by construction).

Exit code: 0 = all checks pass, 1 = broken links or a stale file-tree snapshot.
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

    return check_links()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
