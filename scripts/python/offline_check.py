"""offline_check.py — Validate internal links in the offline/ discovery pack.

Checks that every markdown link in offline/*.md resolves to an existing file
relative to the pack. External URLs and fragment-only anchors are skipped.
Repo-root links (../docs/... etc.) are resolved against the repository root.

Usage:
    python scripts/python/offline_check.py

Exit code: 0 = all links resolve, 1 = one or more broken links.
"""

from __future__ import annotations

import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
OFFLINE_DIR = REPO_ROOT / "offline"
LOG_DIR = REPO_ROOT / ".build"
LOG_DIR.mkdir(parents=True, exist_ok=True)
LOG_PATH = LOG_DIR / f"offline-check-{datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')}.log"

MARKDOWN_LINK = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
FENCE = re.compile(r"^(```|~~~)")

# Link targets that are not file paths.
SKIP_PREFIXES = ("http://", "https://", "mailto:", "#")


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


def main() -> int:
    if not OFFLINE_DIR.is_dir():
        print(f"ERROR: offline/ not found at {OFFLINE_DIR}")
        return 1

    md_files = sorted(OFFLINE_DIR.glob("*.md"))
    broken: list[tuple[str, int, str]] = []
    total_links = 0

    with LOG_PATH.open("w", encoding="utf-8") as log:
        log.write(f"offline_check.py — {datetime.now(timezone.utc).isoformat()}\n")
        for md_file in md_files:
            links = extract_links(md_file)
            log.write(f"\n{md_file.name}: {len(links)} links\n")
            for line_number, target in links:
                total_links += 1
                resolved = resolve_target(md_file, target)
                status = "ok" if resolved is not None else "BROKEN"
                log.write(f"  {status:6} line {line_number:3}: {target}\n")
                if resolved is None:
                    broken.append((md_file.name, line_number, target))

    print(f"Checked {len(md_files)} files, {total_links} links, {len(broken)} broken.")
    if broken:
        for name, line, target in broken:
            print(f"  {name}:{line} -> {target}")
        print(f"Details: {LOG_PATH}")
        return 1

    print(f"All links resolve. Log: {LOG_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
