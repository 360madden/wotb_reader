#!/usr/bin/env python3
"""find-static-roots.py — Offline static root analysis for the WoT Blitz PC binary.

Pure-stdlib PE analysis of the hash-bound 11.19.0.10 `wotblitz.exe` (no game
process, no memory reads). Three capabilities, all of which produce
*evidence-classified hypotheses only* (never runtime offsets):

  --chain ROOT              Verify a claimed pointer-chain root RVA: section
                            membership, reloc-target status, on-disk value
                            shape (module VA / zeroed / string), and whether
                            any .text instruction references it.
  --xref-data [--min-refs N]
                            Discovery: enumerate .data slots referenced by
                            .text instructions (store/load/lea forms), rank by
                            reference count. This is the corrected strategy for
                            finding singleton roots: a root must be read or
                            written by code.
  --rtti SUBSTR             RTTI back-door: find the mangled RTTI name
                            containing SUBSTR, locate its TypeDescriptor, walk
                            to vtables that reference it, and list .data slots
                            pointing at those vtables (named singleton roots).

Examples:
  python tools/find-static-roots.py --chain 0x03E91978
  python tools/find-static-roots.py --xref-data --min-refs 4
  python tools/find-static-roots.py --rtti AvatarContextBattle

All findings are logged to a timestamped file under %TEMP%\\find-static-roots-*.log
and printed to stdout. Exit code 0 on success (even with zero findings), 2 on
usage/parse errors, 1 on internal errors.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import logging
import os
import re
import struct
import sys
from dataclasses import dataclass, field
from typing import Optional

LOG = logging.getLogger("find-static-roots")

DEFAULT_EXE = r"C:\Games\World_of_Tanks_Blitz\wotblitz.exe"
IMAGE_BASE = 0x400000
IMAGE_SIZE = 0x4482000


# --------------------------------------------------------------------------- #
# PE parsing (stdlib only)
# --------------------------------------------------------------------------- #

@dataclass
class Section:
    name: str
    virtual_address: int
    virtual_size: int
    raw_pointer: int
    raw_size: int

    @property
    def end(self) -> int:
        return self.virtual_address + max(self.virtual_size, self.raw_size)


@dataclass
class PeImage:
    path: str
    data: bytes
    image_base: int
    sections: list[Section]
    reloc_targets: set[int]
    text: Optional[Section] = None
    data_sec: Optional[Section] = None
    rdata: Optional[Section] = None

    def section_of(self, rva: int) -> Optional[str]:
        for sec in self.sections:
            if sec.virtual_address <= rva < sec.end:
                return sec.name
        return None

    def rva_to_raw(self, rva: int) -> Optional[int]:
        for sec in self.sections:
            if sec.virtual_address <= rva < sec.end:
                return sec.raw_pointer + (rva - sec.virtual_address)
        return None

    def dword(self, rva: int) -> Optional[int]:
        raw = self.rva_to_raw(rva)
        if raw is None or raw + 4 > len(self.data):
            return None
        return struct.unpack_from("<I", self.data, raw)[0]

    def is_in_module(self, value: int) -> bool:
        return self.image_base <= value < self.image_base + IMAGE_SIZE


def parse_pe(path: str) -> PeImage:
    with open(path, "rb") as handle:
        data = handle.read()
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise ValueError(f"{path} is not a PE file (no MZ header).")

    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew : e_lfanew + 4] != b"PE\0\0":
        raise ValueError(f"{path} has no PE signature at 0x{e_lfanew:X}.")

    coff = e_lfanew + 4
    machine, num_sections, _, _, _, opt_size, _ = struct.unpack_from(
        "<HHIIIHH", data, coff
    )
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    if magic != 0x10B:
        raise ValueError(f"{path} is not PE32 (magic 0x{magic:X}); expected 0x10B.")
    image_base = struct.unpack_from("<I", data, opt + 28)[0]

    sec_off = opt + opt_size
    sections: list[Section] = []
    for index in range(num_sections):
        base = sec_off + index * 40
        name = data[base : base + 8].rstrip(b"\0").decode("latin1")
        vsize, vaddr, rsize, rptr = struct.unpack_from("<IIII", data, base + 8)
        sections.append(
            Section(name=name, virtual_address=vaddr, virtual_size=vsize,
                    raw_pointer=rptr, raw_size=rsize)
        )

    reloc_rva, reloc_size = struct.unpack_from("<II", data, opt + 96 + 5 * 8)
    reloc_targets: set[int] = set()
    reloc_raw = None
    for sec in sections:
        if sec.virtual_address <= reloc_rva < sec.end:
            reloc_raw = sec.raw_pointer + (reloc_rva - sec.virtual_address)
            break
    if reloc_raw is not None:
        pos = reloc_raw
        end = reloc_raw + reloc_size
        while pos + 8 <= end:
            page_rva, block_size = struct.unpack_from("<II", data, pos)
            if block_size == 0:
                break
            count = (block_size - 8) // 2
            for index in range(count):
                entry = struct.unpack_from("<H", data, pos + 8 + index * 2)[0]
                if (entry >> 12) == 3:  # IMAGE_REL_BASED_HIGHLOW
                    reloc_targets.add(page_rva + (entry & 0xFFF))
            pos += block_size

    text = next((s for s in sections if s.name == ".text"), None)
    data_sec = next((s for s in sections if s.name == ".data"), None)
    rdata = next((s for s in sections if s.name == ".rdata"), None)
    return PeImage(path, data, image_base, sections, reloc_targets,
                   text=text, data_sec=data_sec, rdata=rdata)


# --------------------------------------------------------------------------- #
# Capability 1: chain-root verification
# --------------------------------------------------------------------------- #

def _readable_string(pe: PeImage, rva: int, maxlen: int = 64) -> Optional[str]:
    raw = pe.rva_to_raw(rva)
    if raw is None:
        return None
    start = raw
    while start > 0 and 0x20 <= pe.data[start - 1] < 0x7F:
        start -= 1
    end = raw
    while end < len(pe.data) and 0x20 <= pe.data[end] < 0x7F:
        end += 1
    text = pe.data[start:end].decode("latin1", "replace")
    return text if len(text) > 4 else None


def verify_chain_root(pe: PeImage, root: int) -> dict:
    findings: dict = {"root": f"0x{root:08X}", "section": None, "tests": {}}
    section = pe.section_of(root)
    findings["section"] = section
    if section is None:
        findings["tests"]["section_membership"] = {
            "pass": False, "detail": f"0x{root:08X} is outside all sections."}
        return findings

    raw = pe.rva_to_raw(root)
    if raw is not None and raw + 4 <= len(pe.data):
        value = struct.unpack_from("<I", pe.data, raw)[0]
        findings["on_disk_dword"] = f"0x{value:08X}"
    else:
        value = None
        findings["on_disk_dword"] = "unreadable"

    findings["tests"]["reloc_target"] = {
        "pass": root in pe.reloc_targets,
        "detail": ("reloc target (static pointer)" if root in pe.reloc_targets
                   else "not a reloc target (runtime-written or not a pointer)"),
    }

    # .text reference scan for the exact 4-byte operand.
    refs = []
    text_sec = pe.text
    if text_sec is not None:
        tstart = text_sec.raw_pointer
        tend = min(tstart + text_sec.raw_size, len(pe.data))
        needle = struct.pack("<I", root)
        pos = tstart
        while True:
            index = pe.data.find(needle, pos, tend)
            if index < 0:
                break
            refs.append(text_sec.virtual_address + (index - tstart))
            pos = index + 1
    findings["tests"]["text_references"] = {
        "pass": len(refs) > 0,
        "count": len(refs),
        "detail": (f"{len(refs)} .text operand reference(s)"
                   if refs else "no .text instruction ever references this address"),
        "samples": [f"0x{r:08X}" for r in refs[:8]],
    }

    if value is not None:
        if pe.is_in_module(value):
            findings["on_disk_shape"] = (
                f"in-module pointer -> {pe.section_of(value - pe.image_base)}")
        elif value == 0:
            findings["on_disk_shape"] = "zero (runtime-initialized candidate)"
        else:
            string = _readable_string(pe, root)
            findings["on_disk_shape"] = (
                f"string/other: {string!r}" if string else f"opaque 0x{value:08X}")

    passed = (findings["tests"]["reloc_target"]["pass"]
              or len(refs) > 0
              or value == 0)
    findings["verdict"] = (
        "PLAUSIBLE root candidate (reloc target, code-referenced, or zeroed slot)"
        if passed else
        "NOT a static root in this binary (no reloc, no code reference, "
        "non-zero non-pointer bytes)")
    return findings


# --------------------------------------------------------------------------- #
# Capability 2: xref-driven .data discovery
# --------------------------------------------------------------------------- #

# x86-32 forms with an absolute 32-bit operand after the ModRM byte.
_MODRM_DISP32 = {0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D}
_STORE_OPCODES = {0x89, 0xA3}
_LOAD_OPCODES = {0x8B, 0x8D, 0xA1}
_OTHER_OPCODES = {0xC7, 0x68, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xB8, 0xB9}


def discover_data_slots(pe: PeImage, min_refs: int) -> dict:
    data_sec = pe.data_sec
    text = pe.text
    if data_sec is None or text is None:
        raise ValueError("binary has no .data or .text section to analyze")

    data_lo, data_hi = data_sec.virtual_address, data_sec.end
    tstart = text.raw_pointer
    tend = min(tstart + text.raw_size, len(pe.data))
    text_bytes = pe.data[tstart:tend]

    # Pattern matching the top two bytes of any .data address: 0x03B7-0x0409.
    pattern = re.compile(b"[\xb7-\xff]\x03|[\x00-\x09]\x04")

    stores: dict[int, int] = {}
    loads: dict[int, int] = {}
    other: dict[int, int] = {}
    for match in pattern.finditer(text_bytes):
        index = match.start()
        if index - 2 < 0:
            continue
        addr = struct.unpack_from("<I", text_bytes, index - 2)[0]
        if not (data_lo <= addr < data_hi):
            continue
        b3 = text_bytes[index - 3] if index - 3 >= 0 else 0  # ModRM / opcode
        b4 = text_bytes[index - 4] if index - 4 >= 0 else 0  # opcode (two-byte)
        if b3 == 0xA1 or b3 == 0xA3:
            target = (stores if b3 == 0xA3 else loads)
            target[addr] = target.get(addr, 0) + 1
        elif b4 in _STORE_OPCODES and b3 in _MODRM_DISP32:
            stores[addr] = stores.get(addr, 0) + 1
        elif b4 in _LOAD_OPCODES and b3 in _MODRM_DISP32:
            loads[addr] = loads.get(addr, 0) + 1
        elif b4 in _OTHER_OPCODES and b3 in _MODRM_DISP32:
            other[addr] = other.get(addr, 0) + 1
        else:
            other[addr] = other.get(addr, 0) + 1

    ranked: list[dict] = []
    for addr in sorted(set(stores) | set(loads)):
        total = stores.get(addr, 0) + loads.get(addr, 0)
        if total < min_refs:
            continue
        raw = pe.rva_to_raw(addr)
        value = (struct.unpack_from("<I", pe.data, raw)[0]
                 if raw is not None and raw + 4 <= len(pe.data) else None)
        shape = "unknown"
        if value == 0:
            shape = "zero"
        elif pe.is_in_module(value):
            shape = f"-> {pe.section_of(value - pe.image_base)}"
        else:
            string = _readable_string(pe, addr)
            shape = f"string({string[:40]!r})" if string else f"0x{value:08X}"
        ranked.append({
            "address": f"0x{addr:08X}",
            "address_decimal": addr,
            "section": pe.section_of(addr),
            "store_refs": stores.get(addr, 0),
            "load_refs": loads.get(addr, 0),
            "total_refs": total,
            "other_refs": other.get(addr, 0),
            "reloc_target": addr in pe.reloc_targets,
            "on_disk_shape": shape,
        })

    ranked.sort(key=lambda item: (item["store_refs"], item["load_refs"],
                                  item["total_refs"]), reverse=True)
    return {
        "scan": "xref-data",
        "data_range": [f"0x{data_lo:08X}", f"0x{data_hi:08X}"],
        "store_slots": len(stores),
        "load_slots": len(loads),
        "candidates": ranked,
    }


# --------------------------------------------------------------------------- #
# Capability 3: RTTI back-door
# --------------------------------------------------------------------------- #

def _alternation_patterns(rvas: list[int]) -> bytes:
    """Build a regex alternation of little-endian 4-byte dword patterns."""
    patterns = [re.escape(struct.pack("<I", rva)) for rva in rvas]
    return patterns[0] if len(patterns) == 1 else b"|".join(patterns)


def find_rtti(pe: PeImage, substring: str) -> dict:
    """Find mangled RTTI names containing `substring`, then walk TypeDescriptor
    -> vtables -> .data slots that point at those vtables."""
    if pe.rdata is None and pe.data_sec is None:
        raise ValueError("binary has no .rdata/.data for RTTI analysis")

    needle = substring.encode("latin1")
    hits: list[dict] = []
    for sec in (pe.rdata, pe.data_sec):
        if sec is None:
            continue
        start = sec.raw_pointer
        end = min(start + sec.raw_size, len(pe.data))
        pos = start
        while True:
            index = pe.data.find(needle, pos, end)
            if index < 0:
                break
            name_rva = sec.virtual_address + (index - start)
            # Walk back to the start of the NUL-terminated mangled string.
            s = index
            while s > start and pe.data[s - 1] != 0:
                s -= 1
            nul = pe.data.find(b"\0", s)
            name = pe.data[s : nul if nul >= 0 else end].decode("latin1", "replace")
            hits.append({
                "name_rva": f"0x{name_rva:08X}",
                "section": sec.name,
                "mangled": name,
            })
            pos = index + 1

    result = {
        "scan": "rtti",
        "substring": substring,
        "name_hits": hits,
        "typedescriptor_steps": [],
        "col_slots": [],
        "vtable_slots": [],
        "data_roots": [],
    }
    if not hits:
        return result

    # Only mangled RTTI names (MSVC class/union prefix) are TypeDescriptors.
    # Source paths and log strings that merely contain the substring are not.
    mangled_hits = [
        h for h in hits
        if h["mangled"].startswith(".?AV") or h["mangled"].startswith(".?AU")
    ]
    result["name_hits_filtered_to_mangled"] = len(mangled_hits)
    if not mangled_hits:
        LOG.info("RTTI: %d substring hits, none are mangled class names", len(hits))
        return result

    # For each mangled name, the TypeDescriptor is td + 0 (pVFTable at td+0,
    # name at td+8 on MSVC x86).
    td_hits = []
    for hit in mangled_hits:
        name_rva = int(hit["name_rva"], 16)
        td_rva = name_rva - 8
        vft = pe.dword(td_rva)
        if vft is None:
            continue
        td_hits.append({"td_rva": f"0x{td_rva:08X}", "p_vftable": vft})
    result["typedescriptor_steps"] = td_hits
    if not td_hits:
        return result

    # MSVC x86 RTTI hop: vtable[-1] (at vtable-4) holds the Complete Object
    # Locator pointer; COL+12 holds the pTypeDescriptor. So:
    #   pass 1: find COL slots whose COL+12 dword equals a td_rva
    #   pass 2: vtable[-1] slots whose dword equals a COL address
    #   pass 3: vtable base = vtable[-1] slot + 4
    td_rvas = sorted({int(h["td_rva"], 16) for h in td_hits})
    td_patterns = _alternation_patterns(td_rvas)
    td_regex = re.compile(td_patterns)

    col_slots = []
    for sec in (pe.rdata, pe.data_sec):
        if sec is None:
            continue
        start = sec.raw_pointer
        end = min(start + sec.raw_size, len(pe.data))
        for match in td_regex.finditer(pe.data, start, end):
            off = match.start() - start
            rva = sec.virtual_address + off
            value = struct.unpack_from("<I", pe.data, match.start())[0]
            if value not in td_rvas:
                continue
            # This slot is COL+12. COL base is 12 bytes earlier; sanity-check
            # that the COL signature dword at COL+0 is 0 (x86 RTTI).
            col_rva = rva - 12
            signature = pe.dword(col_rva)
            col_slots.append({
                "col_rva": f"0x{col_rva:08X}",
                "col_plus_12_slot": f"0x{rva:08X}",
                "section": sec.name,
                "holds_td": f"0x{value:08X}",
                "signature": signature,
                "plausible": signature == 0,
            })
    result["col_slots"] = col_slots
    col_rvas = {int(c["col_rva"], 16) for c in col_slots if c["plausible"]}
    if not col_rvas:
        return result

    col_patterns = _alternation_patterns(sorted(col_rvas))
    col_regex = re.compile(col_patterns)
    vtable_bases = []
    for sec in (pe.rdata, pe.data_sec):
        if sec is None:
            continue
        start = sec.raw_pointer
        end = min(start + sec.raw_size, len(pe.data))
        for match in col_regex.finditer(pe.data, start, end):
            off = match.start() - start
            rva = sec.virtual_address + off
            value = struct.unpack_from("<I", pe.data, match.start())[0]
            if value not in col_rvas:
                continue
            # A slot whose dword equals a COL address is vtable[-1]; vtable
            # base is one slot later.
            vtable_bases.append({
                "vtable_minus_1_slot": f"0x{rva:08X}",
                "vtable_base_rva": f"0x{rva + 4:08X}",
                "section": sec.name,
                "holds_col": f"0x{value:08X}",
            })
    result["vtable_slots"] = vtable_bases

    vtable_base_rvas = sorted({int(v["vtable_base_rva"], 16) for v in vtable_bases})
    vtable_patterns = _alternation_patterns(vtable_base_rvas)
    vtable_regex = re.compile(vtable_patterns)

    roots = []
    data_sec = pe.data_sec
    if data_sec is not None:
        start = data_sec.raw_pointer
        end = min(start + data_sec.raw_size, len(pe.data))
        for match in vtable_regex.finditer(pe.data, start, end):
            off = match.start() - start
            rva = data_sec.virtual_address + off
            value = struct.unpack_from("<I", pe.data, match.start())[0]
            if value in vtable_base_rvas:
                roots.append({
                    "root_rva": f"0x{rva:08X}",
                    "section": ".data",
                    "points_to": f"0x{value:08X}",
                    "reloc_target": rva in pe.reloc_targets,
                })
    result["data_roots"] = roots
    return result


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #

def _setup_logging(log_path: str) -> None:
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    fmt = logging.Formatter("%(asctime)s %(levelname)s %(message)s")
    file_handler = logging.FileHandler(log_path, encoding="utf-8")
    file_handler.setFormatter(fmt)
    console = logging.StreamHandler(sys.stdout)
    console.setFormatter(fmt)
    root.addHandler(file_handler)
    root.addHandler(console)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Offline static root analysis for the hash-bound wotblitz.exe")
    parser.add_argument("--exe", default=DEFAULT_EXE,
                        help=f"path to wotblitz.exe (default: {DEFAULT_EXE})")
    parser.add_argument("--chain", metavar="RVA[,RVA...]",
                        help="verify claimed root RVAs, comma-separated, e.g. 0x03E91978")
    parser.add_argument("--xref-data", action="store_true",
                        help="discover .data slots referenced by .text")
    parser.add_argument("--min-refs", type=int, default=2,
                        help="minimum total references for xref discovery")
    parser.add_argument("--rtti", metavar="NAME[,NAME...]",
                        help="RTTI back-door for class names, comma-separated, "
                             "e.g. EntityList,VehicleGameLogic")
    parser.add_argument("--json", metavar="PATH",
                        help="write findings as JSON to PATH")
    args = parser.parse_args(argv)

    stamp = _dt.datetime.now(_dt.timezone.utc).strftime("%Y%m%d-%H%M%S")
    log_path = os.path.join(
        os.environ.get("TEMP", "."), f"find-static-roots-{stamp}.log")
    _setup_logging(log_path)

    try:
        if not os.path.isfile(args.exe):
            LOG.error("executable not found: %s", args.exe)
            return 2
        pe = parse_pe(args.exe)
    except (OSError, ValueError) as exc:
        LOG.error("failed to parse PE: %s", exc)
        return 2

    LOG.info("image: %s (base 0x%X, %d sections, %d reloc targets)",
             args.exe, pe.image_base, len(pe.sections), len(pe.reloc_targets))

    results: dict = {}
    ran_any = False

    if args.chain is not None:
        roots = []
        for raw in args.chain.split(","):
            raw = raw.strip()
            try:
                roots.append(int(raw, 0))
            except ValueError:
                LOG.error("invalid --chain RVA: %s", raw)
                return 2
        results["chain"] = []
        for root in roots:
            LOG.info("verify chain root 0x%08X", root)
            verdict = verify_chain_root(pe, root)
            results["chain"].append(verdict)
            LOG.info("chain %s verdict: %s", verdict["root"], verdict["verdict"])
        ran_any = True

    if args.xref_data:
        LOG.info("xref discovery: min refs %d", args.min_refs)
        results["xref_data"] = discover_data_slots(pe, args.min_refs)
        candidates = results["xref_data"]["candidates"]
        LOG.info("xref discovery: %d store slots, %d load slots, %d candidates",
                 results["xref_data"]["store_slots"],
                 results["xref_data"]["load_slots"], len(candidates))
        for candidate in candidates[:15]:
            LOG.info("  %s section=%s store=%d load=%d reloc=%s shape=%s",
                     candidate["address"], candidate["section"],
                     candidate["store_refs"], candidate["load_refs"],
                     candidate["reloc_target"], candidate["on_disk_shape"])
        ran_any = True

    if args.rtti is not None:
        names = [name.strip() for name in args.rtti.split(",") if name.strip()]
        results["rtti"] = {}
        for name in names:
            LOG.info("RTTI back-door: %s", name)
            rtti = find_rtti(pe, name)
            results["rtti"][name] = rtti
            LOG.info("RTTI %s: %d name hits, %d TypeDescriptors, %d vtable slots, %d data roots",
                     name, len(rtti["name_hits"]), len(rtti["typedescriptor_steps"]),
                     len(rtti.get("vtable_slots", [])), len(rtti.get("data_roots", [])))
            for hit in rtti["name_hits"]:
                LOG.info("  name: %s @ %s", hit["mangled"], hit["name_rva"])
            for root in rtti.get("data_roots", []):
                LOG.info("  data root: %s points_to=%s reloc=%s",
                         root["root_rva"], root["points_to"], root["reloc_target"])
        ran_any = True

    if not ran_any:
        parser.print_help()
        return 2

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(results, handle, indent=2)
        LOG.info("findings written to %s", args.json)

    LOG.info("log written to %s", log_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
