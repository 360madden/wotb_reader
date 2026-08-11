#!/usr/bin/env python3
"""e2e_smoke.py — End-to-end smoke test for the WotB Treader stack.

Publishes the web host, starts it on a random port, hits every API endpoint,
validates JSON response schemas, and reports pass/fail. Cleans up on exit.

Usage:
  python scripts/python/e2e_smoke.py

Prerequisites:
  - .NET SDK 10.0.302
  - At least one replay imported (via CLI 'import' or synthetic replay)
  - No existing process on the test port

Output: timestamped log to .build/e2e-smoke-<datetime>.log
Exit code: 0 on all pass, 1 on any failure.
"""

import subprocess
import sys
import os
import json
import time
import urllib.request
import urllib.error
import socket
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
LOG_DIR = REPO_ROOT / ".build"
PUBLISH_DIR = REPO_ROOT / ".build" / "publish"
DATA_ROOT = REPO_ROOT / ".data"

# ── Helpers ──────────────────────────────────────────────────────────────────

def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_log(log_path: Path, msg: str) -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")[:23]
    line = f"[{ts}] {msg}"
    print(line)
    with open(log_path, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def find_free_port() -> int:
    """Find an available loopback port."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def http_get(url: str, timeout: float = 10, parse_json: bool = True) -> tuple[int, Any]:
    """GET a URL, return (status_code, body_or_parsed_json).

    When parse_json is True (default), the response body is parsed as JSON.
    Set parse_json=False for HTML endpoints like dashboard pages.
    """
    headers = {"Accept": "application/json"} if parse_json else {}
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read().decode("utf-8")
            if parse_json and body:
                return resp.status, json.loads(body)
            return resp.status, body
    except urllib.error.HTTPError as e:
        return e.code, None
    except Exception as e:
        return -1, str(e)


def http_post_json(url: str, payload: dict, timeout: float = 10) -> tuple[int, Any]:
    """POST JSON, returning (status_code, parsed_json_body). Parses the body
    even on error statuses so fail-closed endpoints can be asserted."""
    headers = {"Accept": "application/json", "Content-Type": "application/json"}
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            body = resp.read().decode("utf-8")
            return resp.status, json.loads(body) if body else None
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(body) if body else None
        except json.JSONDecodeError:
            return e.code, None
    except Exception as e:
        return -1, str(e)


# ── Assertions ───────────────────────────────────────────────────────────────

class CheckFailed(Exception):
    pass


def check(log_path: Path, name: str, condition: bool, detail: str = "") -> None:
    status = "PASS" if condition else "FAIL"
    msg = f"  [{status}] {name}"
    if detail and not condition:
        msg += f" — {detail}"
    write_log(log_path, msg)
    if not condition:
        raise CheckFailed(name)


def check_status(log_path: Path, name: str, status: int, expected: int = 200,
                 detail: str = "") -> None:
    ok = status == expected
    d = detail or f"HTTP {status} (expected {expected})"
    check(log_path, name, ok, d)


def check_field(log_path: Path, name: str, obj: Any, field: str,
                expected: Any = None, present_only: bool = False) -> None:
    if obj is None:
        check(log_path, name, False, "response body is null")
        return
    if field not in obj:
        check(log_path, name, False, f"missing field '{field}'")
        return
    if not present_only and expected is not None:
        actual = obj[field]
        ok = actual == expected
        check(log_path, name, ok,
              f"expected '{field}'={expected!r}, got {actual!r}" if not ok else "")
    else:
        check(log_path, name, True)


# ── Publisher ────────────────────────────────────────────────────────────────

def publish_host(log_path: Path) -> bool:
    write_log(log_path, "")
    write_log(log_path, "[Phase 1] Publishing web host...")

    proc = subprocess.run(
        ["dotnet", "publish",
         str(REPO_ROOT / "src" / "WotBTreader.Host.Web"),
         "-c", "Release",
         "-o", str(PUBLISH_DIR),
         "--no-restore"],
        capture_output=True, text=True, cwd=str(REPO_ROOT), timeout=180,
    )

    if proc.returncode != 0:
        write_log(log_path, f"ERROR: dotnet publish failed (exit {proc.returncode})")
        for line in proc.stderr.splitlines()[-10:]:
            write_log(log_path, f"  {line}")
        return False

    write_log(log_path, "  Publish succeeded.")
    return True


# ── Host Runner ───────────────────────────────────────────────────────────────

class HostRunner:
    def __init__(self, log_path: Path, port: int):
        self.log_path = log_path
        self.port = port
        self.base_url = f"http://127.0.0.1:{port}"
        self.process: subprocess.Popen | None = None
        self._stdout_thread: threading.Thread | None = None

    def start(self) -> bool:
        env = os.environ.copy()
        env["Paths__ApplicationDataRoot"] = str(DATA_ROOT)
        # Host uses ConfigureKestrel() which binds to Web:Port, not --urls
        env["Web__Port"] = str(self.port)

        dll = PUBLISH_DIR / "WotBTreader.Host.Web.dll"
        if not dll.exists():
            write_log(self.log_path, f"ERROR: Host DLL not found at {dll}")
            return False

        write_log(self.log_path, "")
        write_log(self.log_path, f"[Phase 2] Starting host on port {self.port}...")

        self.process = subprocess.Popen(
            ["dotnet", str(dll)],
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            cwd=str(REPO_ROOT),
        )

        # Drain stdout in background so buffer doesn't fill
        def drain():
            assert self.process and self.process.stdout
            for _ in self.process.stdout:
                pass

        self._stdout_thread = threading.Thread(target=drain, daemon=True)
        self._stdout_thread.start()

        return self._wait_ready()

    def _wait_ready(self, timeout: float = 60) -> bool:
        deadline = time.time() + timeout
        attempt = 0
        while time.time() < deadline:
            attempt += 1
            code, body = http_get(f"{self.base_url}/api/v1/doctor", timeout=3)
            if code == 200 and isinstance(body, dict):
                write_log(self.log_path, f"  Host ready (attempt {attempt}).")
                return True
            if attempt == 1 or attempt % 10 == 0:
                write_log(self.log_path,
                          f"  Waiting for host... (attempt {attempt}, HTTP {code})")
            time.sleep(1)

        write_log(self.log_path, "ERROR: Host did not start within timeout.")
        return False

    def stop(self) -> None:
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
            write_log(self.log_path, "  Host stopped.")

    def url(self, path: str) -> str:
        return f"{self.base_url}{path}"


# ── Test Runner ──────────────────────────────────────────────────────────────

def run_api_tests(log_path: Path, host: HostRunner) -> int:
    write_log(log_path, "")
    write_log(log_path, "[Phase 3] API endpoint tests")
    write_log(log_path, "-" * 50)

    failures = 0
    total = 0

    def test(name: str, fn) -> None:
        nonlocal failures, total
        total += 1
        try:
            fn()
        except CheckFailed:
            failures += 1
        except Exception as e:
            failures += 1
            write_log(log_path, f"  [FAIL] {name} — {e}")

    # ── Doctor ──
    def t_doctor():
        code, body = http_get(host.url("/api/v1/doctor"))
        check_status(log_path, "doctor HTTP 200", code)
        check_field(log_path, "doctor schemaVersion", body, "schemaVersion", "1")
        check_field(log_path, "doctor checks array", body, "checks", present_only=True)
        checks = body.get("checks", [])
        check(log_path, "doctor has 5 checks", len(checks) == 5,
              f"got {len(checks)}")
        for c in checks:
            check(log_path, f"doctor check {c.get('id','?')} status",
                  c.get("status") in ("pass", "warn"),
                  f"status={c.get('status')}")

    test("GET /api/v1/doctor", t_doctor)

    # ── Sessions ──
    def t_sessions():
        code, body = http_get(host.url("/api/v1/sessions?limit=5"))
        check_status(log_path, "sessions HTTP 200", code)
        check_field(log_path, "sessions offset", body, "offset", 0)
        check_field(log_path, "sessions limit", body, "limit", 5)
        check_field(log_path, "sessions count", body, "count", present_only=True)
        check_field(log_path, "sessions items", body, "items", present_only=True)
        items = body.get("items", [])
        if items:
            item = items[0]
            check_field(log_path, "session has decodeRun", item, "decodeRun", present_only=True)
            check_field(log_path, "session has session", item, "session", present_only=True)

    test("GET /api/v1/sessions", t_sessions)

    # ── Game state ──
    def t_game_state():
        code, body = http_get(host.url("/api/v1/game/state"))
        check_status(log_path, "game state HTTP 200", code)
        check_field(log_path, "game state verificationState", body, "verificationState", present_only=True)
        check_field(log_path, "game state reasonCode", body, "reasonCode", present_only=True)

    test("GET /api/v1/game/state", t_game_state)

    # ── Map boundaries ──
    def t_boundaries():
        code, body = http_get(host.url("/api/v1/maps/boundaries"))
        check_status(log_path, "map boundaries HTTP 200", code)
        check(log_path, "map boundaries is list", isinstance(body, list),
              f"type={type(body).__name__}")

    test("GET /api/v1/maps/boundaries", t_boundaries)

    # ── CAM-005/006 surface: the new routes exist and fail closed ──
    def t_camera_pose_unverified():
        # The camera-pose POST is a mutation: without a capability it must be
        # rejected (401) by the middleware — proving the route is registered
        # and cannot be reached without the local auth.
        code, body = http_post_json(host.url("/api/v1/game/discover/camera-pose"), {})
        check_status(log_path, "POST /discover/camera-pose 401 without capability", code, expected=401)

    test("POST /api/v1/game/discover/camera-pose (capability-gated)", t_camera_pose_unverified)

    def t_frame_missing_session():
        # A random session id must 404, proving the CAM-006 frame route is
        # registered and fail-closed without a decoded session.
        import uuid
        code, _ = http_get(host.url(f"/api/v1/sessions/{uuid.uuid4()}/frame"))
        check_status(log_path, "GET /sessions/{id}/frame 404 without session", code, expected=404)

    test("GET /api/v1/sessions/{id}/frame (fail-closed)", t_frame_missing_session)

    # ── Dashboard pages ──
    for page, path in [("dashboard", "/"), ("comparisons", "/comparisons"),
                        ("diagnostics", "/diagnostics")]:
        def _make(page=page, path=path):
            code, _ = http_get(host.url(path), parse_json=False)
            check_status(log_path, f"{page} page HTTP 200", code)
        test(f"GET {path} ({page})", _make)

    # ── Summary ──
    write_log(log_path, "-" * 50)
    write_log(log_path, f"Results: {total - failures}/{total} passed, {failures} failed")
    return failures


# ── Main ─────────────────────────────────────────────────────────────────────

def main() -> int:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    log_path = LOG_DIR / f"e2e-smoke-{timestamp}.log"
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    write_log(log_path, "=" * 60)
    write_log(log_path, "WotB Treader — E2E Smoke Test")
    write_log(log_path, f"Started: {now_iso()}")
    write_log(log_path, "=" * 60)
    write_log(log_path, f"Repo root: {REPO_ROOT}")
    write_log(log_path, f"Data root: {DATA_ROOT}")
    write_log(log_path, f"Log:       {log_path}")

    if not (REPO_ROOT / "WotBTreader.sln").exists():
        write_log(log_path, "ERROR: WotBTreader.sln not found — wrong directory?")
        return 1

    # Phase 1: Publish
    if not publish_host(log_path):
        return 1

    # Phase 2: Start host
    port = find_free_port()
    host = HostRunner(log_path, port)

    try:
        if not host.start():
            return 1

        # Phase 3: Run tests
        failures = run_api_tests(log_path, host)
        return 1 if failures > 0 else 0

    finally:
        host.stop()
        write_log(log_path, "")
        write_log(log_path, f"Finished: {now_iso()}")


if __name__ == "__main__":
    sys.exit(main())
