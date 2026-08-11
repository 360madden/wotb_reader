#!/usr/bin/env python3
"""verify-camera-projection.py — validates the W2S projection of the decoded
viewpoint tank through the live memory camera (CAM-001 v7 round evidence).

Consumes a CAM-001 aggregate JSON (schema v7, `roundSamples[]`). Each round
carries the memory camera pose (GameCamera posA + yaw/pitch), the memory tank
position (when the resolver was up), and the decoded tank at the yaw-aligned
time. The validator projects the DECODED tank through the MEMORY camera using
the exact `WorldToScreen.Project` math (src/WotBTreader.Core/Overlay/
WorldToScreen.cs) and checks the third-person look-at property:

  - Look-at angle: the angle between the camera forward (yaw/pitch, roll 0)
    and the camera->tank direction. A third-person camera aims at the tank,
    so this is small (<= LOOK_AT_TOLERANCE_DEG, default 8).
  - Center distance: the projected tank lands near viewport center (<=
    CENTER_TOLERANCE of the half-viewport) across the FOV band
    (40..90 deg vertical, per the CAM-009 config findings) — the result
    must not be an artifact of one FOV.
  - Pitch diagnostic: expected pitch = atan2(camera->tank vertical delta,
    horizontal distance) is reported alongside the memory pitch, so a wrong
    pitch convention shows up as a large expected-vs-memory gap.

Exit codes: 0 = verified, 1 = validation failed, 2 = evidence missing (no
evaluable rounds). `--self-test` runs a synthetic fixture (no aggregate
needed) and is CI-safe.

Usage:
  python scripts/python/verify-camera-projection.py [aggregate.json] [--json out.json]
  python scripts/python/verify-camera-projection.py --self-test
"""

import argparse
import glob
import json
import math
import os
import sys

LOOK_AT_TOLERANCE_DEG = 8.0   # third-person camera aims at the tank
CENTER_TOLERANCE = 0.25       # |offset| / half-viewport at the center
# CAM-009 (2026-08-11): the install's optionsGlobal.yaml pins the engine
# default fov at 64 (horizontal, horizontal->vertical radius coefficient
# 0.73 => ~47 deg vertical; player slider default 54, third-person offset
# 8). Sweep the plausible vertical band; the look-at check is FOV-
# independent, so the sweep mainly guards the center-distance check.
FOV_BAND_DEG = (40.0, 47.0, 64.0, 90.0)
PASS_RATIO = 0.7              # fraction of evaluable rounds that must pass
VIEWPORT = (1920, 1080)

# ---------------------------------------------------------------------------
# Exact mirror of WorldToScreen.Project (Core/Overlay/WorldToScreen.cs).
# Forward follows the packet convention (yaw 0 -> +Z):
#   f = (sin yaw * cos pitch, sin pitch, cos yaw * cos pitch)
# ---------------------------------------------------------------------------


def project(eye, yaw, pitch, fov_deg, width, height, world):
    """Returns (screen_x, screen_y, depth) or None when behind the camera."""
    fov = math.radians(fov_deg)
    cos_yaw, sin_yaw = math.cos(yaw), math.sin(yaw)
    cos_pitch, sin_pitch = math.cos(pitch), math.sin(pitch)
    fx = sin_yaw * cos_pitch
    fy = sin_pitch
    fz = cos_yaw * cos_pitch
    # Right = normalize(cross(forward, worldUp)); up = (0, 1, 0).
    rx, ry, rz = cos_yaw, 0.0, -sin_yaw
    # Up = cross(forward, right).
    ux = fy * rz - fz * ry
    uy = fz * rx - fx * rz
    uz = fx * ry - fy * rx

    dx = world[0] - eye[0]
    dy = world[1] - eye[1]
    dz = world[2] - eye[2]
    cam_x = dx * rx + dy * ry + dz * rz
    cam_y = dx * ux + dy * uy + dz * uz
    depth = dx * fx + dy * fy + dz * fz
    if depth <= 0:
        return None

    focal = (height / 2.0) / math.tan(fov / 2.0)
    screen_x = width / 2.0 + (cam_x / depth) * focal
    screen_y = height / 2.0 - (cam_y / depth) * focal
    return (screen_x, screen_y, depth)


def forward(yaw, pitch):
    cos_yaw, sin_yaw = math.cos(yaw), math.sin(yaw)
    cos_pitch, sin_pitch = math.cos(pitch), math.sin(pitch)
    return (sin_yaw * cos_pitch, sin_pitch, cos_yaw * cos_pitch)


def look_at_angle_deg(eye, yaw, pitch, world):
    """Angle between the camera forward and the camera->tank direction."""
    f = forward(yaw, pitch)
    d = (world[0] - eye[0], world[1] - eye[1], world[2] - eye[2])
    norm = math.sqrt(sum(v * v for v in d))
    if norm <= 1e-9:
        return 0.0
    dot = (f[0] * d[0] + f[1] * d[1] + f[2] * d[2]) / norm
    dot = max(-1.0, min(1.0, dot))
    return math.degrees(math.acos(dot))


def center_distance(screen, width, height):
    """Distance from viewport center as a fraction of the half-viewport."""
    ndc_x = (screen[0] - width / 2.0) / (width / 2.0)
    ndc_y = (screen[1] - height / 2.0) / (height / 2.0)
    return math.sqrt(ndc_x * ndc_x + ndc_y * ndc_y)


def evaluate_round(round_sample, width, height):
    """Returns a diagnostic dict, or None when the round is not evaluable."""
    camera = round_sample.get("camera")
    decoded = round_sample.get("decodedTank")
    if not camera or not decoded or not all(
        k in camera for k in ("x", "y", "z", "yawRadians", "pitchRadians")
    ) or not all(k in decoded for k in ("x", "y", "z")):
        return None

    eye = (camera["x"], camera["y"], camera["z"])
    yaw = camera["yawRadians"]
    pitch = camera["pitchRadians"]
    world = (decoded["x"], decoded["y"], decoded["z"])

    look_at = look_at_angle_deg(eye, yaw, pitch, world)

    # Expected pitch if the camera aims exactly at the tank (diagnostic for
    # the pitch convention; not part of the pass/fail).
    dx = world[0] - eye[0]
    dy = world[1] - eye[1]
    dz = world[2] - eye[2]
    horizontal = math.sqrt(dx * dx + dz * dz)
    expected_pitch = math.degrees(math.atan2(dy, horizontal)) if horizontal > 1e-9 else None

    projections = {}
    behind = False
    for fov in FOV_BAND_DEG:
        point = project(eye, yaw, pitch, fov, width, height, world)
        if point is None:
            behind = True
            continue
        projections[fov] = center_distance(point, width, height)

    passed = (
        look_at <= LOOK_AT_TOLERANCE_DEG
        and not behind
        and all(distance <= CENTER_TOLERANCE for distance in projections.values())
    )
    return {
        "alignedDecodedSeconds": round_sample.get("alignedDecodedSeconds"),
        "memoryTankSource": round_sample.get("memoryTankSource"),
        "cameraPosition": [round(x, 3) for x in eye],
        "decodedTank": [round(x, 3) for x in world],
        "lookAtAngleDeg": round(look_at, 3),
        "memoryPitchDeg": round(math.degrees(pitch), 3),
        "expectedPitchDeg": round(expected_pitch, 3) if expected_pitch is not None else None,
        "tankBehindCamera": behind,
        "centerDistanceByFov": {str(k): round(v, 4) for k, v in sorted(projections.items())},
        "passed": passed,
    }


def verify(aggregate, width, height):
    rounds = aggregate.get("roundSamples") or []
    results = [evaluate_round(r, width, height) for r in rounds]
    evaluable = [r for r in results if r is not None]
    if not evaluable:
        return {
            "schema": aggregate.get("schema"),
            "verdict": "camera-state-consistent" if aggregate.get("verdict") else "unknown",
            "roundsTotal": len(rounds),
            "roundsEvaluable": 0,
            "roundsPassed": 0,
            "passRatio": None,
            "status": "evidence-missing",
            "rounds": [],
        }
    passed_count = sum(1 for r in evaluable if r["passed"])
    ratio = passed_count / len(evaluable)
    return {
        "schema": aggregate.get("schema"),
        "verdict": aggregate.get("verdict"),
        "roundsTotal": len(rounds),
        "roundsEvaluable": len(evaluable),
        "roundsPassed": passed_count,
        "passRatio": round(ratio, 3),
        "status": "verified" if ratio >= PASS_RATIO else "failed",
        "rounds": evaluable,
    }


def self_test():
    width, height = VIEWPORT
    failures = []

    def check(name, cond, detail):
        if not cond:
            failures.append((name, detail))

    # 1. Camera behind and above the tank aiming at it: look-at ~0, tank at
    #    viewport center across the FOV band.
    round_ok = {
        "camera": {"x": 0.0, "y": 5.0, "z": -20.0, "yawRadians": 0.0,
                   "pitchRadians": math.atan2(-5.0, 20.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    result = evaluate_round(round_ok, width, height)
    check("look-at fixture produced an evaluable round", result is not None, result)
    check("look-at fixture passes", result is not None and result["passed"], result)
    if result:
        check("look-at angle ~0", result["lookAtAngleDeg"] <= 0.5, result)
        check("tank at center", all(v <= 0.01 for v in result["centerDistanceByFov"].values()), result)

    # 2. Camera yaw rotated 90 deg away from the tank: look-at ~90, projection
    #    behind the camera -> must fail.
    round_wrong = {
        "camera": {"x": 0.0, "y": 5.0, "z": -20.0, "yawRadians": math.pi / 2.0,
                   "pitchRadians": math.atan2(-5.0, 20.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    wrong = evaluate_round(round_wrong, width, height)
    check("wrong-yaw fixture is evaluable", wrong is not None, wrong)
    check("wrong-yaw fixture fails", wrong is not None and not wrong["passed"], wrong)
    if wrong:
        check("wrong-yaw look-at ~90", wrong["lookAtAngleDeg"] >= 80.0, wrong)

    # 3. Correct yaw but pitch 0 (no look-down): the tank sits well below
    #    center and the look-at angle exceeds the tolerance -> must fail.
    round_no_pitch = {
        "camera": {"x": 0.0, "y": 5.0, "z": -20.0, "yawRadians": 0.0,
                   "pitchRadians": 0.0},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    no_pitch = evaluate_round(round_no_pitch, width, height)
    check("no-pitch fixture is evaluable", no_pitch is not None, no_pitch)
    check("no-pitch fixture fails", no_pitch is not None and not no_pitch["passed"], no_pitch)
    if no_pitch:
        check("no-pitch look-at exceeds tolerance", no_pitch["lookAtAngleDeg"] > LOOK_AT_TOLERANCE_DEG, no_pitch)
        check("no-pitch expected pitch diagnostic", no_pitch["expectedPitchDeg"] is not None
              and abs(no_pitch["expectedPitchDeg"] - math.degrees(math.atan2(-5.0, 20.0))) < 0.01, no_pitch)

    if failures:
        for name, detail in failures:
            print(f"self-test FAIL: {name}: {json.dumps(detail, default=str)}")
        return 1
    print("self-test PASS: look-at, wrong-yaw, and no-pitch fixtures behave as expected.")
    return 0


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("aggregate", nargs="?", default=None,
                        help="CAM-001 aggregate JSON (default: newest .data/cam001-camera-state-verify-*.json)")
    parser.add_argument("--json", dest="json_out", default=None,
                        help="Write the full report JSON to this path")
    parser.add_argument("--self-test", action="store_true",
                        help="Run the built-in synthetic fixture and exit")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()

    path = args.aggregate
    if path is None:
        matches = sorted(glob.glob(
            os.path.join(".data", "cam001-camera-state-verify-*.json")))
        if not matches:
            print("verify-camera-projection: no aggregate found; pass one or run --self-test.", file=sys.stderr)
            return 2
        path = matches[-1]
        print(f"verify-camera-projection: using {path}")

    try:
        with open(path, "r", encoding="utf-8") as handle:
            aggregate = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"verify-camera-projection: cannot read {path}: {exc}", file=sys.stderr)
        return 2

    report = verify(aggregate, *VIEWPORT)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)

    print(f"verify-camera-projection: schema={report['schema']} "
          f"evaluable={report['roundsEvaluable']}/{report['roundsTotal']} "
          f"passed={report['roundsPassed']} ratio={report['passRatio']} "
          f"status={report['status']}")
    for r in report.get("rounds", []):
        print(f"  t={r['alignedDecodedSeconds']} lookAt={r['lookAtAngleDeg']} deg "
              f"pitch(mem/exp)={r['memoryPitchDeg']}/{r['expectedPitchDeg']} "
              f"center={r['centerDistanceByFov']} {'PASS' if r['passed'] else 'FAIL'}")

    if report["status"] == "verified":
        return 0
    if report["status"] == "evidence-missing":
        print("verify-camera-projection: no evaluable rounds (tank never resolved) — evidence missing.", file=sys.stderr)
        return 2
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
