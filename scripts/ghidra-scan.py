#!/usr/bin/env python3
"""ghidra-scan.py — Run FindOffsets.java on the analyzed Ghidra project.

Writes timestamped logs to .build/ghidra-scan-<datetime>.log so results
can be inspected reliably regardless of when/how this script was launched.

Usage: python scripts/ghidra-scan.py
"""

import subprocess
import sys
import os
from datetime import datetime, timezone

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOG_DIR = os.path.join(REPO_ROOT, ".build")
SCRIPT_DIR = os.path.join(REPO_ROOT, "tools", "ghidra-scripts")
OUTPUT_FILE = os.path.join(SCRIPT_DIR, "ghidra-offset-candidates.json")

GHIDRA = r"C:\work\tools\ghidra_12.1.2_PUBLIC"
PROJECT = r"C:\work\tools\ghidra-projects"
JAVA_HOME = r"C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot"
ANALYZE_HEADLESS = os.path.join(GHIDRA, "support", "analyzeHeadless.bat")


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_log(log_path: str, msg: str):
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")[:23]
    line = f"[{ts}] {msg}"
    print(line)
    with open(log_path, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def main():
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    log_path = os.path.join(LOG_DIR, f"ghidra-scan-{timestamp}.log")
    os.makedirs(LOG_DIR, exist_ok=True)

    write_log(log_path, "=" * 60)
    write_log(log_path, "Ghidra Scan — FindOffsets.java")
    write_log(log_path, f"Started: {now_iso()}")
    write_log(log_path, "=" * 60)
    write_log(log_path, f"GHIDRA:     {GHIDRA}")
    write_log(log_path, f"PROJECT:    {PROJECT}")
    write_log(log_path, f"JAVA_HOME:  {JAVA_HOME}")
    write_log(log_path, f"LOG:        {log_path}")
    write_log(log_path, f"OUTPUT:     {OUTPUT_FILE}")
    write_log(log_path, "")

    # Verify prerequisites
    if not os.path.isfile(ANALYZE_HEADLESS):
        write_log(log_path, f"ERROR: analyzeHeadless.bat not found at {ANALYZE_HEADLESS}")
        sys.exit(1)

    project_file = os.path.join(PROJECT, "WotBlitz.gpr")
    if not os.path.isfile(project_file):
        write_log(log_path, f"ERROR: Ghidra project not found at {project_file}")
        write_log(log_path, "Run ghidra-offsets.bat first to import and analyze.")
        sys.exit(1)

    if not os.path.isfile(os.path.join(JAVA_HOME, "bin", "java.exe")):
        write_log(log_path, f"ERROR: Java not found at {JAVA_HOME}\\bin\\java.exe")
        sys.exit(1)

    # Remove any previous output
    if os.path.isfile(OUTPUT_FILE):
        os.remove(OUTPUT_FILE)
        write_log(log_path, "Removed previous candidates file.")

    # Build command
    env = os.environ.copy()
    env["JAVA_HOME"] = JAVA_HOME

    cmd = [
        ANALYZE_HEADLESS,
        PROJECT,
        "WotBlitz",
        "-process", "wotblitz.exe",
        "-noanalysis",  # Skip re-analysis — project is already analyzed
        "-postScript", "FindOffsets.java",
        "-scriptPath", SCRIPT_DIR,
    ]

    write_log(log_path, f"Command: {' '.join(cmd)}")
    write_log(log_path, "")
    write_log(log_path, "[Phase 1] Loading analyzed project...")

    try:
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            env=env,
            text=True,
            encoding="utf-8",
            errors="replace",
        )

        for line in proc.stdout:
            line = line.rstrip()
            if line:
                write_log(log_path, line)

        proc.wait()

        write_log(log_path, "")
        write_log(log_path, f"Exit code: {proc.returncode}")

        if proc.returncode != 0:
            write_log(log_path, "ERROR: Ghidra exited with non-zero code.")
            sys.exit(proc.returncode)

        if os.path.isfile(OUTPUT_FILE):
            size = os.path.getsize(OUTPUT_FILE)
            write_log(log_path, f"SUCCESS: Candidates file written ({size} bytes)")
            write_log(log_path, f"         {OUTPUT_FILE}")
        else:
            write_log(log_path, "WARNING: No candidates file produced.")
            write_log(log_path, "         Check the log above for script errors.")

    except FileNotFoundError:
        write_log(log_path, f"ERROR: Could not execute {ANALYZE_HEADLESS}")
        sys.exit(1)
    except Exception as e:
        write_log(log_path, f"ERROR: {e}")
        sys.exit(1)

    write_log(log_path, "")
    write_log(log_path, f"Finished: {now_iso()}")


if __name__ == "__main__":
    main()
