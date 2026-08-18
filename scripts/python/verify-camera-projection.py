#!/usr/bin/env python3
"""verify-camera-projection.py — validates the W2S projection of the decoded
viewpoint tank through the live memory camera (CAM-001 v7 round evidence).

Consumes a CAM-001 aggregate JSON (schema v7, `roundSamples[]`). Each round
carries the memory camera pose (GameCamera posA + yaw/pitch), the memory tank
position (when the resolver was up), and the decoded tank at the yaw-aligned
time. The validator projects the tank through the MEMORY camera using the
exact `WorldToScreen.Project` math (src/WotBTreader.Core/Overlay/
WorldToScreen.cs) and checks the third-person look-at property. The PRIMARY
projection target is the MEMORY tank (same wall time / memory space as the
camera — the W2S overlay is inherently memory-space); the decoded tank at the
yaw-aligned time is a cross-check only, because the replay-clock label skew
can put the yaw-aligned time far from the actual read time (corrected
2026-08-11). It also reports the camera-basis coherence (memory-side
mode-vs-pose discriminator) and, when `-CaptureWindow` was used, the derived
sky/terrain scalars as a render-mode hint:

  - Look-at angle: the angle between the camera forward (yaw/pitch, roll 0)
    and the camera->AIM-POINT direction. CAM-013 (2026-08-11): the WoTB
    chase camera aims at the TURRET-LEVEL aim point of the player's tank
    (~1.9 m above the hull center — the aim reticle sits above the tank),
    NOT the hull center; measuring against the hull center misclassifies a
    genuine chase view (tank ~2 m below the aim point, level memory pitch)
    as non-chase.
  - Center distance: the projected aim point lands near viewport center (<=
    CENTER_TOLERANCE of the half-viewport) across the FOV band
    (40..90 deg vertical, per the CAM-009 config findings).
  - Pitch diagnostic: expected pitch = atan2(camera->aim-point vertical
    delta, horizontal distance) vs the memory pitch; the aim-point PITCH
    GAP is the robust pose discriminator and gates the pass/fail (at chase
    distances — eye 1-3 m from the tank — the angular look-at / center
    measures are degenerate). The hull-center framing is reported
    separately (in chase the tank lands below center).

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

# CAM-013 (2026-08-11): the WoTB third-person chase camera aims at the
# turret-level aim point of the player's tank, pinned at ~1.9 m above the
# hull center by the 2026-08-11 captures: the eye sits 1.7-2.0 m above the
# hull on every chase round and the level memory pitch is only
# chase-consistent when the target is at the eye's height (residual height
# uncertainty < ~0.7 m from the pitch-gap bound at the 1-2 m read ranges).
AIM_POINT_HEIGHT_METERS = 1.9
# Memory pitch vs pitch-to-aim-point: <= this gap classifies the round as a
# coherent chase aim (CAM-013 data: 0.1-15.6 deg on 16/18 chase rounds;
# v7b battle-intro reads are 86-100 deg and correctly stay non-chase).
PITCH_GAP_TOLERANCE_DEG = 20.0
# Look-at is degenerate below this eye->aim distance (sub-meter position
# noise dominates the direction); near rounds are gated on pitch gap only.
NEAR_AIM_METERS = 4.0

# ---------------------------------------------------------------------------
# Exact mirror of WorldToScreen.Project (Core/Overlay/WorldToScreen.cs).
# Forward follows the packet convention (yaw 0 -> +Z):
#   f = (sin yaw * cos pitch, sin pitch, cos yaw * cos pitch)
# ---------------------------------------------------------------------------


def project(eye, yaw, pitch, fov_deg, width, height, world):
    """Returns (screen_x, screen_y, depth) or None when behind the camera.
    Mirrors WorldToScreen.Project: any non-finite input fails closed (None),
    so NaN can never reach the pixel coordinates."""
    if not all(math.isfinite(v) for v in (*eye, yaw, pitch, fov_deg, width, height, *world)):
        return None
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


def _orientation_from_basis(basis, raw_yaw, raw_pitch):
    """Mirrors the C# W2S seam (LiveFrameProjector.BuildCamera): the
    camera's world orientation is authoritative from the view-basis rows —
    forward = -row1, up = row2 (CAM-012, look-at 0.4-6.7 deg at the
    turret-level aim point). Row1 of the COMPACTED basis is indices
    [3:6] (the PS1 persists the raw stride-4 12-float region; the C#
    coordinator compacts row0=[0:3], row1=[3:6], row2=[6:9]). This
    function accepts either layout: for a 12-float stride-4 array row1 is
    indices [4:7], for the compacted 9-float it is [3:6].

    Converts the world forward into the packet yaw/pitch convention
    (fy = sin pitch, yaw 0 -> +Z, +pi/2 -> +X). Falls back to the raw
    yaw/pitch fields (DAVA, documented best-effort) when the basis is
    missing or non-finite. Returns (yaw, pitch).
    """
    row1 = None
    if basis and len(basis) >= 9:
        # Stride-4 (12 floats, pads at 3/7/11) vs compacted (9 floats).
        idx = 4 if len(basis) >= 12 else 3
        candidate = [basis[idx], basis[idx + 1], basis[idx + 2]]
        if all(isinstance(v, (int, float)) and math.isfinite(v)
               for v in candidate):
            row1 = candidate
    if row1 is not None:
        fx, fy, fz = -row1[0], -row1[1], -row1[2]
        length = math.sqrt(fx * fx + fy * fy + fz * fz)
        if length > 1e-6:
            fx, fy, fz = fx / length, fy / length, fz / length
            pitch = math.asin(max(-1.0, min(1.0, fy)))
            yaw = math.atan2(fx, fz)
            if math.isfinite(yaw) and math.isfinite(pitch):
                return yaw, pitch
    return raw_yaw, raw_pitch


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


def _finite_vec(vec):
    """Component mask of a possibly NaN-padded vector."""
    return [k for k in range(len(vec)) if math.isfinite(vec[k])]


def basis_coherent(basis, yaw, pitch, tol_deg=15.0):
    """Checks the persisted view-basis floats (+0x80..0xB0) are a coherent
    camera basis.

    Layout VERIFIED 2026-08-11 on real dumps (CAM-001 v7b): the region is
    a row-major 3x4 view matrix with 16-byte stride — row0 at +0x80
    (indices [0:3]), row1 at +0x90 ([4:7]), row2 at +0xA0 ([8:11]), and
    the zero padding at +0x8C/+0x9C. Row0 equals the camera forward under
    the DAVA left-handed convention: row0 = (fx, -fy, -fz) where
    fwd = forward(yaw, pitch) (measured dot 1.0000 across all 6 rounds of
    the 2026-08-11 session). Rows are orthonormal with r0 x r1 = r2.

    Coherence = (a) rows unit-length, (b) pairwise-orthogonal, (c) the
    cross product r0 x r1 equals r2 (componentwise, finite components
    only), and (d) one row matches forward(yaw, pitch) under any
    per-axis sign flip (max-over-signs dot >= cos(tol)). Legacy 10-float
    captures (row2.z at +0xA8 unread) are verified on the finite
    components only and reported as such.

    Returns (coherent, details) where details explains the verdict.
    """
    if not basis or len(basis) < 9 or not all(
        isinstance(v, (int, float)) and math.isfinite(v) for v in basis[:9]
    ):
        return False, "basis missing or non-finite"

    # Stride-4 rows: [0:3] (+0x80), [4:7] (+0x90), [8:11] (+0xA0). Legacy
    # 10-float captures pad row2.z (+0xA8) with NaN.
    rows = [
        list(basis[0:3]),
        list(basis[4:7]),
        list(basis[8:11]) if len(basis) >= 11 else [basis[8], basis[9], float("nan")],
    ]
    fwd = forward(yaw, pitch)
    finite_rows = [_finite_vec(r) for r in rows]
    if len(finite_rows[2]) < 2:
        return False, "basis rows incomplete (row2 lacks its x/y components)"

    # Legacy 10-float captures never read row2.z (+0xA8). A partial row's
    # own unit length / pair-wise dot cannot be verified (the missing z
    # term is exactly what makes it orthogonal), so for partial rows we
    # rely on the cross-product identity r0 x r1 = r2, which pins row2
    # completely: if r0, r1 are unit and orthogonal and r0 x r1 = r2,
    # then r2 is automatically unit and orthogonal to both.
    complete = len(basis) >= 12 and len(finite_rows[2]) == 3

    # (a) unit length — complete rows only; (b) orthogonality — only
    # between complete rows (partial pairs are implied by the cross check).
    complete_rows = [r for r, comps in zip(rows, finite_rows)
                     if len(comps) == 3]
    for r in complete_rows:
        norm = math.sqrt(sum(v * v for v in r))
        if abs(norm - 1.0) > 0.02:
            return False, f"basis row not unit length (norm {norm:.3f})"
    if len(complete_rows) >= 2:
        for i in range(len(complete_rows)):
            for j in range(i + 1, len(complete_rows)):
                dot = sum(complete_rows[i][k] * complete_rows[j][k]
                          for k in range(3))
                if abs(dot) > 0.02:
                    return False, f"basis rows not orthogonal (dot {dot:.3f})"

    # (c) cross product r0 x r1 == r2 on finite components.
    xprod = [
        rows[0][1] * rows[1][2] - rows[0][2] * rows[1][1],
        rows[0][2] * rows[1][0] - rows[0][0] * rows[1][2],
        rows[0][0] * rows[1][1] - rows[0][1] * rows[1][0],
    ]
    xfail = [
        k for k in finite_rows[2]
        if abs(xprod[k] - rows[2][k]) > 0.03
    ]
    if xfail:
        return False, f"r0 x r1 != r2 on components {xfail}"

    # (d) one row matches forward(yaw, pitch) up to per-axis sign flips.
    best = None
    for row_idx, r in enumerate(rows):
        comps = finite_rows[row_idx]
        dot_abs = sum(abs(r[k] * fwd[k]) for k in comps)
        if dot_abs >= math.cos(math.radians(tol_deg)):
            angle = math.degrees(math.acos(min(1.0, dot_abs)))
            best = (row_idx, angle, len(comps))
            break
    if best is None:
        return False, "no row matches forward(yaw, pitch) under sign flips"

    partial = " (legacy 10-float capture: row2.z unverified)" if not complete else ""
    return True, (
        f"orthonormal stride-4 rows, r0 x r1 = r2, forward=row{best[0]}, "
        f"angle {best[1]:.2f} deg{partial}"
    )


def classify_mode(screen, look_at, expected_pitch, memory_pitch):
    """Classifies the camera state: chase (tank-centered third-person),
    non-chase (elevated/detached — the 2026-08-11 honest-negative
    signature), high (elevated with visible sky band), or unknown.
    Returns (mode, hint) — hint is diagnostic text, never a verdict.

    Order matters: the memory-side pitch-to-aim gap branch (no pixel
    dependence) fires first, because the sky-luminance branch is
    scene-dependent (savanna dusk skies never pass the >0.5 row-luminance
    sky test). CAM-013 (2026-08-11): a chase camera aims at the
    TURRET-LEVEL AIM POINT (~1.9 m above the hull center), so look-at ~0 /
    memory pitch ~= pitch-to-aim; the non-chase state aims elsewhere
    (large look-at AND memory pitch far from pitch-to-aim). expected_pitch
    and memory_pitch are RADIANS (the pre-CAM-013 caller passed degrees,
    which made the gap comparison unit-inconsistent)."""
    if look_at <= 8.0:
        sky_txt = _screen_summary(screen)
        return "chase", f"look-at {look_at:.1f} deg{sky_txt}"
    if (
        expected_pitch is not None and memory_pitch is not None
        and abs(memory_pitch - expected_pitch) > math.radians(PITCH_GAP_TOLERANCE_DEG)
    ):
        return "non-chase", (
            f"look-at {look_at:.1f} deg, memory pitch {math.degrees(memory_pitch):.1f} "
            f"vs pitch-to-aim {math.degrees(expected_pitch):.1f} deg"
        )
    if screen:
        sky = screen.get("skyFraction")
        horizon = screen.get("horizonRow")
        if isinstance(sky, (int, float)) and isinstance(horizon, (int, float)):
            if sky > 0.15 and 0.3 < horizon < 0.8:
                return "high", f"look-at {look_at:.1f} deg, sky {sky:.2f}, horizon {horizon:.2f}"
    return "unknown", _screen_summary(screen) or "no screen scalars (run with -CaptureWindow)"


def _screen_summary(screen):
    if not screen:
        return None
    sky = screen.get("skyFraction")
    horizon = screen.get("horizonRow")
    if isinstance(sky, (int, float)) and isinstance(horizon, (int, float)):
        return f", sky {sky:.2f}, horizon {horizon:.2f}"
    return None


def evaluate_round(round_sample, width, height):
    """Returns a diagnostic dict, or None when the round is not evaluable.

    CAM-010 (2026-08-11): the GameCamera position is stored (x, z, y) —
    world Y and Z swapped — so the world eye is the yz-swap of the
    persisted camera position (see the swap comment in the body). This
    overturns CAM-004's "23.57 m third-person offset" (that value was
    sqrt(2)*|tank.z - tank.y| at the read moment, an artifact of the
    swapped read, reproduced to sub-meter on v7b/v7c).

    CORRECTED 2026-08-11 (CAM-001 v7 root-cause follow-up): the W2S
    projection is inherently MEMORY-space — the overlay consumes the
    memory camera pose and memory tank/entity positions at the same wall
    time. The memory tank (module-rooted, same wall time as the camera) is
    therefore the PRIMARY projection target; the decoded tank at the
    yaw-aligned time is only a cross-check. The old code projected the
    decoded tank, whose yaw-aligned time can be WRONG by the replay-clock
    skew (e.g. 30-40 s aligned while the reads were at ~180 s), which
    silently corrupted the look-at/center check."""
    camera = round_sample.get("camera")
    memory_tank = round_sample.get("memoryTank")
    decoded = round_sample.get("decodedTank")
    if not camera or not all(
        k in camera for k in ("x", "y", "z", "yawRadians", "pitchRadians")
    ):
        return None
    if not memory_tank and not decoded:
        return None
    if memory_tank and not all(k in memory_tank for k in ("x", "y", "z")):
        memory_tank = None
    if decoded and not all(k in decoded for k in ("x", "y", "z")):
        decoded = None
    if not memory_tank and not decoded:
        return None

    # CAM-010 (2026-08-11): the GameCamera stores its position at
    # +0x38/+0x3C/+0x40 as (x, z, y) — the world Y and Z are SWAPPED
    # relative to the tank/entity space (proven on v7b+v7c: yz-swapped
    # posA sits 2.1-3.6 m from the decoded tank, sub-meter, while the
    # as-read distance was 113-206 m and CAM-004's "23.57 m third-person
    # offset" was the sqrt(2)*|tank.z - tank.y| artifact). The orientation
    # fields (yaw/pitch/basis) keep their stored convention. So the world
    # eye is the yz-swap of the stored position.
    eye = (camera["x"], camera["z"], camera["y"])
    # CAM-012 (2026-08-11): the authoritative camera orientation is the
    # basis — forward = -row1, up = row2 (look-at collapsed to 0.4-6.7 deg,
    # avg 1.7 deg, at the turret-level aim point). The raw yaw/pitch fields
    # are DAVA left-handed, NOT the packet convention WorldToScreen uses
    # (no yaw/pitch sign combo reproduces the aim direction). Mirror the
    # C# W2S seam: derive packet-convention yaw/pitch from forward = -row1
    # (row1 = compacted basis[3..5]), falling back to the raw fields when
    # the basis is missing or non-finite.
    yaw, pitch = _orientation_from_basis(
        round_sample.get("basis"), camera["yawRadians"], camera["pitchRadians"])
    # Primary projection target: the memory tank (same wall time / memory
    # space as the camera). The decoded tank is the cross-check.
    world = (memory_tank["x"], memory_tank["y"], memory_tank["z"]) if memory_tank else (
        decoded["x"], decoded["y"], decoded["z"])
    cross_world = (decoded["x"], decoded["y"], decoded["z"]) if decoded and memory_tank else None

    # CAM-013 (2026-08-11): the WoTB third-person chase camera aims at the
    # TURRET-LEVEL AIM POINT of the player's tank (~1.9 m above the hull
    # center), not the hull center — the aim reticle sits above the tank
    # and the tank occupies the lower-center of the frame. Measuring
    # look-at / expected-pitch / center against the hull center
    # misclassified every 2026-08-11 capture as non-chase: the hull sits
    # ~2 m below the aim point, so pitch-to-hull read -35..-63 deg while
    # the memory pitch read level. All 18 reads of the 091/091b/092/v7c
    # captures classify CHASE against the aim point (pitch gap 0.1-15.6
    # deg, look-at 1.8-20 deg) and the v7b battle-intro reads stay
    # non-chase (+88 deg pitch — a real cinematic). At chase distances
    # (eye ~1-3 m from the tank) the angular look-at / center measures are
    # degenerate, so `passed` is gated on the aim-point PITCH GAP; look-at
    # and center stay reported diagnostics.
    aim = (world[0], world[1] + AIM_POINT_HEIGHT_METERS, world[2])

    look_at = look_at_angle_deg(eye, yaw, pitch, aim)

    # Expected pitch if the camera aims exactly at the turret-level aim
    # point (radians — the diagnostic + classify_mode input; converted to
    # degrees in the report).
    dx = aim[0] - eye[0]
    dy = aim[1] - eye[1]
    dz = aim[2] - eye[2]
    horizontal = math.sqrt(dx * dx + dz * dz)
    expected_pitch_rad = math.atan2(dy, horizontal) if horizontal > 1e-9 else None

    # Aim-point projection (the gating target) + hull-center framing
    # (diagnostic: in the chase view the tank lands below center).
    projections = {}
    behind = False
    for fov in FOV_BAND_DEG:
        point = project(eye, yaw, pitch, fov, width, height, aim)
        if point is None:
            behind = True
            continue
        projections[fov] = center_distance(point, width, height)
    tank_framing = {}
    for fov in FOV_BAND_DEG:
        point = project(eye, yaw, pitch, fov, width, height, world)
        if point is None:
            continue
        tank_framing[fov] = center_distance(point, width, height)
    # Cross-check: decoded-tank projection (only meaningful when the
    # yaw-alignment is trusted; reported, not gating).
    cross_projections = {}
    if cross_world is not None:
        for fov in FOV_BAND_DEG:
            point = project(eye, yaw, pitch, fov, width, height, cross_world)
            if point is None:
                continue
            cross_projections[fov] = center_distance(point, width, height)

    pitch_gap_deg = (
        math.degrees(abs(pitch - expected_pitch_rad))
        if expected_pitch_rad is not None else None)
    # At chase distances (aim point <= NEAR_AIM_METERS from the eye) the
    # angular yaw look-at is degenerate (the target is essentially at the
    # eye — sub-meter position noise dominates the direction), so near
    # rounds are gated on the pitch gap alone; farther rounds additionally
    # require a small look-at (catches wrong-yaw states, e.g. the ~20 m
    # camera cuts on 091b r4 / 091 r5).
    near_aim = math.sqrt(sum((a - b) ** 2 for a, b in zip(eye, aim))) <= NEAR_AIM_METERS
    passed = (
        pitch_gap_deg is not None
        and pitch_gap_deg <= PITCH_GAP_TOLERANCE_DEG
        and not behind
        and (near_aim or look_at <= LOOK_AT_TOLERANCE_DEG)
    )
    # CAM-001 v7 root-cause follow-up: the walked object is a coherent
    # camera iff its basis rows are orthonormal and one row matches
    # yaw/pitch — this is the memory-side half of the mode-vs-pose
    # discriminator and does NOT assume the chase view (the look-at check
    # only passes in the chase state, so a high-state camera is reported
    # coherent-but-not-aimed, which is exactly the honest-negative shape
    # seen on 2026-08-11).
    coherent, basis_detail = basis_coherent(
        round_sample.get("basis"), yaw, pitch)
    mode, mode_hint = classify_mode(
        round_sample.get("screen"), look_at, expected_pitch_rad, pitch)
    return {
        "alignedDecodedSeconds": round_sample.get("alignedDecodedSeconds"),
        "memoryTankSource": round_sample.get("memoryTankSource"),
        "cameraPosition": [round(x, 3) for x in eye],
        "decodedTank": [round(x, 3) for x in (
            (decoded["x"], decoded["y"], decoded["z"]) if decoded else world)],
        "projectionTankSource": "memory" if memory_tank else "decoded",
        "crossDecodedCenterByFov": {str(k): round(v, 4) for k, v in sorted(cross_projections.items())},
        "lookAtAngleDeg": round(look_at, 3),
        "memoryPitchDeg": round(math.degrees(pitch), 3),
        "expectedPitchDeg": round(math.degrees(expected_pitch_rad), 3) if expected_pitch_rad is not None else None,
        "pitchGapDeg": round(pitch_gap_deg, 3) if pitch_gap_deg is not None else None,
        "aimPointHeightMeters": AIM_POINT_HEIGHT_METERS,
        "tankBehindCamera": behind,
        "centerDistanceByFov": {str(k): round(v, 4) for k, v in sorted(projections.items())},
        "tankFramingByFov": {str(k): round(v, 4) for k, v in sorted(tank_framing.items())},
        "cameraCoherent": coherent,
        "basisDetail": basis_detail,
        "renderMode": mode,
        "modeHint": mode_hint,
        "screen": round_sample.get("screen"),
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

    # 1. Camera behind and above the tank aiming at the TURRET-LEVEL AIM
    #    POINT (hull + 1.9 m, CAM-013): look-at ~0, aim point at viewport
    #    center across the FOV band -> passes. The camera dict is the
    #    STORED layout (x, z, y) — the world eye (0, 5, -20) is stored as
    #    (0, -20, 5) per the CAM-010 yz-swap finding.
    round_ok = {
        "camera": {"x": 0.0, "y": -20.0, "z": 5.0, "yawRadians": 0.0,
                   "pitchRadians": math.atan2(-3.1, 20.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    result = evaluate_round(round_ok, width, height)
    check("look-at fixture produced an evaluable round", result is not None, result)
    check("look-at fixture passes", result is not None and result["passed"], result)
    if result:
        check("look-at angle ~0", result["lookAtAngleDeg"] <= 0.5, result)
        check("aim point at center", all(v <= 0.01 for v in result["centerDistanceByFov"].values()), result)

    # 2. Camera yaw rotated 90 deg away from the tank: look-at ~90, projection
    #    behind the camera -> must fail. (Stored layout, same as above.)
    round_wrong = {
        "camera": {"x": 0.0, "y": -20.0, "z": 5.0, "yawRadians": math.pi / 2.0,
                   "pitchRadians": math.atan2(-3.1, 20.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    wrong = evaluate_round(round_wrong, width, height)
    check("wrong-yaw fixture is evaluable", wrong is not None, wrong)
    check("wrong-yaw fixture fails", wrong is not None and not wrong["passed"], wrong)
    if wrong:
        check("wrong-yaw look-at ~90", wrong["lookAtAngleDeg"] >= 80.0, wrong)

    # 3. Correct yaw but pitch pointing AWAY from the aim point (up 30 deg vs
    #    pitch-to-aim -8.8 deg): the pitch gap exceeds the tolerance -> must
    #    fail.
    round_no_aim = {
        "camera": {"x": 0.0, "y": -20.0, "z": 5.0, "yawRadians": 0.0,
                   "pitchRadians": math.radians(30.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 60.0,
        "memoryTankSource": "fixture",
    }
    no_aim = evaluate_round(round_no_aim, width, height)
    check("no-aim fixture is evaluable", no_aim is not None, no_aim)
    check("no-aim fixture fails", no_aim is not None and not no_aim["passed"], no_aim)
    if no_aim:
        check("no-aim pitch gap exceeds tolerance", no_aim["pitchGapDeg"] is not None
              and no_aim["pitchGapDeg"] > PITCH_GAP_TOLERANCE_DEG, no_aim)
        check("no-aim expected pitch diagnostic", no_aim["expectedPitchDeg"] is not None
              and abs(no_aim["expectedPitchDeg"] - math.degrees(math.atan2(-3.1, 20.0))) < 0.01, no_aim)

    # 4. CAM-013 chase signature: the level-camera geometry measured on every
    #    2026-08-11 capture — eye 1.9 m above the hull, ~2 m back, level
    #    pitch, aim point at the eye's height. The pitch gap is ~0 -> passes;
    #    the hull-center framing reports the tank below center (expected
    #    chase framing, NOT a miss). This is the regression the pre-CAM-013
    #    hull-center acceptance misjudged as non-chase.
    round_chase = {
        "camera": {"x": 0.0, "y": -2.0, "z": 1.9, "yawRadians": 0.0,
                   "pitchRadians": 0.0},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 130.0,
        "memoryTankSource": "fixture",
    }
    chase = evaluate_round(round_chase, width, height)
    check("CAM-013 chase fixture is evaluable", chase is not None, chase)
    check("CAM-013 chase fixture passes", chase is not None and chase["passed"], chase)
    if chase:
        check("CAM-013 pitch gap ~0", chase["pitchGapDeg"] is not None and chase["pitchGapDeg"] <= 1.0, chase)
        check("CAM-013 tank framing below center", all(
            v > CENTER_TOLERANCE for v in chase["tankFramingByFov"].values()), chase)

    # 5. Battle-intro cinematic (v7b shape): pitch +88 deg vs pitch-to-aim
    #    ~0 — the pitch gap is huge -> must fail (genuine non-chase state).
    round_intro = {
        "camera": {"x": 0.0, "y": -2.0, "z": 1.9, "yawRadians": 0.0,
                   "pitchRadians": math.radians(88.0)},
        "decodedTank": {"x": 0.0, "y": 0.0, "z": 0.0},
        "alignedDecodedSeconds": 50.0,
        "memoryTankSource": "fixture",
    }
    intro = evaluate_round(round_intro, width, height)
    check("battle-intro fixture is evaluable", intro is not None, intro)
    check("battle-intro fixture fails", intro is not None and not intro["passed"], intro)

    # 6. Concrete-pixel mirror of the C# WorldToScreenTests fixtures. The
    #    validator claims an exact mirror of WorldToScreen.Project; these
    #    assertions pin that claim to the C# implementation, so a drift in
    #    either side fails here instead of silently misjudging a live
    #    session.
    #    PointStraightAhead_ProjectsToCenter: (0,0,0), yaw 0, pitch 0,
    #    fov 90 deg, 1920x1080, world (0,0,10) -> (960, 540), depth 10.
    center_px = project((0.0, 0.0, 0.0), 0.0, 0.0, 90.0, 1920.0, 1080.0, (0.0, 0.0, 10.0))
    check("C# mirror fixture projects", center_px is not None, center_px)
    if center_px:
        check("C# mirror X == 960", abs(center_px[0] - 960.0) < 1e-6, center_px)
        check("C# mirror Y == 540", abs(center_px[1] - 540.0) < 1e-6, center_px)
        check("C# mirror depth == 10", abs(center_px[2] - 10.0) < 1e-6, center_px)
    #    YawQuarterTurn_FacesPositiveX: yaw +pi/2, world (10,0,0) -> center.
    quarter = project((0.0, 0.0, 0.0), math.pi / 2.0, 0.0, 90.0, 1920.0, 1080.0, (10.0, 0.0, 0.0))
    check("C# yaw-quarter-turn fixture projects", quarter is not None, quarter)
    if quarter:
        check("C# yaw-quarter X == 960", abs(quarter[0] - 960.0) < 1e-6, quarter)
        check("C# yaw-quarter Y == 540", abs(quarter[1] - 540.0) < 1e-6, quarter)

    # 7. Basis coherence (stride-4 DAVA layout verified 2026-08-11): an
    #    orthonormal row set whose row0 = (fx,-fy,-fz) of forward(yaw,pitch)
    #    is coherent; scrambled rows are not; legacy 10-float captures
    #    (row2.z unread) are verified on finite components.
    yaw0, pitch0 = 0.0, 0.0  # forward = +Z
    fwd = forward(yaw0, pitch0)
    row0 = (fwd[0], -fwd[1], -fwd[2])  # DAVA: (fx, -fy, -fz) = (0, 0, -1)
    row1 = (0.0, 1.0, 0.0)
    row2 = (row0[1] * row1[2] - row0[2] * row1[1],
            row0[2] * row1[0] - row0[0] * row1[2],
            row0[0] * row1[1] - row0[1] * row1[0])  # r0 x r1 = (1, 0, 0)
    coherent_basis = [row0[0], row0[1], row0[2], 0.0,
                      row1[0], row1[1], row1[2], 0.0,
                      row2[0], row2[1], row2[2], 0.0]
    ok_basis, detail_basis = basis_coherent(coherent_basis, yaw0, pitch0)
    check("basis coherent when stride-4 orthonormal + DAVA forward row", ok_basis, detail_basis)
    check("basis forward is row0", "forward=row0" in detail_basis, detail_basis)
    legacy_basis, detail_legacy = basis_coherent(coherent_basis[:10], yaw0, pitch0)
    check("legacy 10-float basis coherent on finite components", legacy_basis, detail_legacy)
    check("legacy detail flags partial verification", "legacy" in detail_legacy, detail_legacy)
    scrambled = [0.9, 0.0, 0.0, 0.0, 0.9, 0.0, 0.0, 0.0, 0.9, 0.0, 0.0, 0.0]
    bad_basis, detail_bad = basis_coherent(scrambled, yaw0, pitch0)
    check("basis NOT coherent when rows not orthonormal", not bad_basis, detail_bad)
    missing_basis, detail_missing = basis_coherent(None, yaw0, pitch0)
    check("basis NOT coherent when missing", not missing_basis, detail_missing)

    # 5b. Orientation from basis (CAM-012 mirror of the C# W2S seam):
    #    forward = -row1 of the stride-4 view matrix; row1 = (0,0,-1) must
    #    give yaw 0 / pitch 0 in the packet convention REGARDLESS of the raw
    #    yaw/pitch fields (DAVA). Both the 12-float stride-4 layout and the
    #    C#-compacted 9-float layout must agree; a non-finite/missing basis
    #    falls back to the raw fields.
    stride4_row1_forward_z = [1.0, 0.0, 0.0, 0.0,
                              0.0, 0.0, -1.0, 0.0,
                              0.0, 1.0, 0.0, 0.0]
    y, p = _orientation_from_basis(stride4_row1_forward_z, 1.2345, 0.7)
    check("basis row1 (stride-4) forward +Z -> yaw 0", abs(y) < 1e-9, y)
    check("basis row1 (stride-4) forward +Z -> pitch 0", abs(p) < 1e-9, p)
    compacted = [1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 1.0, 0.0]
    y2, p2 = _orientation_from_basis(compacted, 1.2345, 0.7)
    check("basis row1 (compacted) forward +Z -> yaw 0", abs(y2) < 1e-9, y2)
    check("basis row1 (compacted) forward +Z -> pitch 0", abs(p2) < 1e-9, p2)
    # Forward = +X (row1 = (-1,0,0)) -> yaw +pi/2.
    forward_x = [0.0, 0.0, -1.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0]
    y3, p3 = _orientation_from_basis(forward_x, 0.0, 0.0)
    check("basis row1 forward +X -> yaw +pi/2", abs(y3 - math.pi / 2.0) < 1e-9, y3)
    # Missing / non-finite basis -> raw fields fallback.
    y4, p4 = _orientation_from_basis(None, 0.5, -0.25)
    check("missing basis falls back to raw yaw", abs(y4 - 0.5) < 1e-12, y4)
    check("missing basis falls back to raw pitch", abs(p4 + 0.25) < 1e-12, p4)
    bad = [float("nan"), 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]
    y5, p5 = _orientation_from_basis(bad, 0.5, -0.25)
    check("non-finite basis falls back to raw yaw", abs(y5 - 0.5) < 1e-12, y5)
    check("non-finite basis falls back to raw pitch", abs(p5 + 0.25) < 1e-12, p5)

    # 8. Mode classification: high sky + mid horizon => high camera; tiny
    #    look-at => chase; level camera far from pitch-to-tank => non-chase
    #    (the 2026-08-11 honest-negative signature); no signals => unknown.
    mode_high, hint_high = classify_mode(
        {"skyFraction": 0.4, "horizonRow": 0.5}, 30.0, None, None)
    check("mode classifies high camera", mode_high == "high", (mode_high, hint_high))
    mode_chase, hint_chase = classify_mode(
        {"skyFraction": 0.05, "horizonRow": 0.4}, 2.0, None, None)
    check("mode classifies chase camera", mode_chase == "chase", (mode_chase, hint_chase))
    mode_nonchase, hint_nonchase = classify_mode(
        {"skyFraction": 0.0, "horizonRow": 0.39},
        50.0, math.radians(-46.0), math.radians(-3.0))
    check("mode classifies non-chase from pitch gap without sky",
          mode_nonchase == "non-chase", (mode_nonchase, hint_nonchase))
    mode_unknown, hint_unknown = classify_mode(None, 30.0, None, None)
    check("mode unknown without screen scalars", mode_unknown == "unknown", (mode_unknown, hint_unknown))

    if failures:
        for name, detail in failures:
            print(f"self-test FAIL: {name}: {json.dumps(detail, default=str)}")
        return 1
    print("self-test PASS: aim-point look-at, wrong-yaw, no-aim pitch, CAM-013 chase signature, battle-intro cinematic, C# mirror, basis-coherence, and mode-classifier fixtures behave as expected.")
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
        coherent = r.get("cameraCoherent")
        mode = r.get("renderMode") or "n/a"
        print(f"  t={r['alignedDecodedSeconds']} lookAt={r['lookAtAngleDeg']} deg "
              f"pitch(mem/exp)={r['memoryPitchDeg']}/{r['expectedPitchDeg']} "
              f"center={r['centerDistanceByFov']} "
              f"coherent={coherent} mode={mode} "
              f"{'PASS' if r['passed'] else 'FAIL'}")

    if report["status"] == "verified":
        return 0
    if report["status"] == "evidence-missing":
        print("verify-camera-projection: no evaluable rounds (tank never resolved) — evidence missing.", file=sys.stderr)
        return 2
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
