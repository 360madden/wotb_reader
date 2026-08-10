#!/usr/bin/env python3
"""workstream-lock.py — cooperative serialization for parallel agents.

Single-writer resources must never be touched by two agents at once. This
helper is the convention: acquire before using, release when done. Lock files
live in .build/locks/ (gitignored) and carry owner pid + purpose, so a stale
lock from a dead process is detected and can be broken with --force.

Usage:
  python scripts/workstream-lock.py acquire <resource> [--purpose TEXT] [--force]
  python scripts/workstream-lock.py release <resource>
  python scripts/workstream-lock.py status [<resource>]
  python scripts/workstream-lock.py break <resource>

Resources (defined in docs/operations/parallel-workstreams.md):
  ghidra-project  — WotBlitz.rep headless passes (one at a time)
  docs            — docs/ + handoffs writes (one agent at a time)
  live-session    — the gated live queue (strictly serialized)
"""

import json
import os
import secrets
import socket
import sys
import time
from datetime import datetime, timezone

LOCK_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".build", "locks")

RESOURCES = {"ghidra-project", "docs", "live-session"}


def _now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _lock_path(resource: str) -> str:
    return os.path.join(LOCK_DIR, f"{resource}.lock")


def _pid_alive(pid: int | None) -> bool:
    if pid is None or pid <= 0:
        return False
    if sys.platform == "win32":
        # Windows does not support os.kill(pid, 0) (raises WinError 87).
        # Query the process directly; STILL_ACTIVE (259) means it is running.
        import ctypes

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return False
        try:
            exit_code = ctypes.c_ulong()
            ok = kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code))
            return bool(ok) and exit_code.value == 259
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False


def _read_lock(path: str):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def acquire(resource: str, purpose: str, force: bool, worker_pid: int | None) -> int:
    if resource not in RESOURCES:
        print(f"ERROR: unknown resource '{resource}'. Known: {sorted(RESOURCES)}", file=sys.stderr)
        return 2
    os.makedirs(LOCK_DIR, exist_ok=True)
    path = _lock_path(resource)
    owner = _read_lock(path)
    if owner is not None:
        if (_pid_alive(owner.get("pid", -1)) or _pid_alive(owner.get("worker_pid", -1))) and not force:
            print(
                f"BLOCKED: '{resource}' locked by pid {owner.get('pid')} "
                f"(worker {owner.get('worker_pid', '-')}, {owner.get('purpose', '?')}) "
                f"since {owner.get('acquired_at', '?')} — token {owner.get('token', '?')}",
                file=sys.stderr,
            )
            return 1
        print(f"WARNING: breaking stale lock '{resource}' (owners dead or --force)")
    token = secrets.token_hex(4)
    payload = {
        "pid": os.getpid(),
        "worker_pid": worker_pid,
        "token": token,
        "host": socket.gethostname(),
        "acquired_at": _now_iso(),
        "purpose": purpose or "unspecified",
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)
    print(f"ACQUIRED '{resource}' token={token} (pid {os.getpid()}) — {payload['purpose']}")
    print(f"  release with: python scripts/workstream-lock.py release {resource} --token {token}")
    return 0


def release(resource: str, token: str | None, force: bool) -> int:
    path = _lock_path(resource)
    owner = _read_lock(path)
    if owner is None:
        print(f"NOTE: '{resource}' not locked — nothing to release")
        return 0
    if not force and owner.get("token") != token:
        print(
            f"ERROR: '{resource}' owned with token {owner.get('token')}, got {token} — "
            f"refusing to release another agent's lock (use --force to override)",
            file=sys.stderr,
        )
        return 1
    os.remove(path)
    print(f"RELEASED '{resource}'")
    return 0


def status(resource: str | None) -> int:
    if resource is not None and resource not in RESOURCES:
        print(f"ERROR: unknown resource '{resource}'", file=sys.stderr)
        return 2
    targets = [resource] if resource else sorted(RESOURCES)
    for r in targets:
        path = _lock_path(r)
        owner = _read_lock(path)
        if owner is None:
            print(f"{r}: free")
        elif _pid_alive(owner.get("pid", -1)) or _pid_alive(owner.get("worker_pid", -1)):
            print(f"{r}: LOCKED by pid {owner.get('pid')} (worker {owner.get('worker_pid', '-')}) — {owner.get('purpose', '?')} since {owner.get('acquired_at', '?')}")
        else:
            print(f"{r}: STALE (owners dead) — break with --force / break")
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    cmd = sys.argv[1]
    resource = sys.argv[2] if len(sys.argv) > 2 else None
    if cmd == "status" and resource is None:
        return status(None)
    purpose = None
    force = False
    token = None
    worker_pid = None
    if "--purpose" in sys.argv:
        purpose = sys.argv[sys.argv.index("--purpose") + 1]
    if "--force" in sys.argv:
        force = True
    if "--token" in sys.argv:
        token = sys.argv[sys.argv.index("--token") + 1]
    if "--worker-pid" in sys.argv:
        worker_pid = int(sys.argv[sys.argv.index("--worker-pid") + 1])
    if cmd == "acquire":
        return acquire(resource, purpose, force, worker_pid)
    if cmd == "release":
        return release(resource, token, force)
    if cmd == "status":
        return status(resource if resource in RESOURCES else None)
    if cmd == "break":
        path = _lock_path(resource)
        if os.path.exists(path):
            os.remove(path)
            print(f"BROKE lock '{resource}'")
            return 0
        print(f"NOTE: '{resource}' not locked")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
