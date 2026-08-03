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
  --refs RVA[,RVA...]       Decode every .text reference site for the given
                            roots (store / load / lea / imm forms) — the
                            offline equivalent of Find-what-writes.
  --fields RVA[,RVA...]     Dump plausible typed member candidates (float32 /
                            double / in-module pointer / small int32) in a
                            .data window around the given roots, as relative
                            displacements for a live session.
  --record-map BASE,STRIDE[,COUNT]
                            Classify each member of a repeating record array
                            (e.g. the 0x50-byte EH/handler family found around
                            0x03FA0C74) and list runtime-initialized
                            (zero-on-disk) slots.
  --vtables [--min-slots N]  Discover vtable candidates in .rdata
                            (consecutive .text-pointer runs of >= N slots),
                            resolve each RTTI class name via the COL chain,
                            and list .data slots pointing at vtable bases
                            (named singleton roots).

Examples:
  python tools/find-static-roots.py --chain 0x03E91978
  python tools/find-static-roots.py --xref-data --min-refs 4
  python tools/find-static-roots.py --rtti AvatarContextBattle
  python tools/find-static-roots.py --refs 0x03FA0C74 --fields 0x03FA0C74
  python tools/find-static-roots.py --record-map 0x03FA0C20,0x50,8
  python tools/find-static-roots.py --vtables --min-slots 5

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


def _text_refs(pe: PeImage, root: int) -> list[int]:
    """RVA list of every .text instruction containing `root` as a 4-byte
    operand (absolute disp32, moffs32, or imm32)."""
    refs: list[int] = []
    text_sec = pe.text
    if text_sec is None:
        return refs
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
    return refs


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
    refs = _text_refs(pe, root)
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
# Capability 4: reference-site instruction decoding (offline "what writes this")
# --------------------------------------------------------------------------- #

# x86-32 register names for the ModRM.reg field and the B8+Bx opcodes.
_REGS = ("eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi")

# Opcodes that take an absolute disp32 or moffs32 operand (single-byte forms
# that do NOT use ModRM): A1 = mov eax,[moffs], A3 = mov [moffs],eax.
_MOFFS_OPS = {0xA1: "mov eax,[abs]", 0xA3: "mov [abs],eax"}
# One-byte opcodes whose ModRM byte follows immediately (ModRM.disp32 form).
_MODRM_OPS = {
    0x88: "mov [m+disp32],r8",
    0x89: "mov [m+disp32],r32",
    0x8A: "mov r8,[m+disp32]",
    0x8B: "mov r32,[m+disp32]",
    0x8D: "lea r32,[m+disp32]",
    0xC7: "mov [m+disp32],imm32",
}
# 0x0F-prefixed two-byte opcodes with a following ModRM disp32 operand.
_MODRM_OPS_2BYTE = {
    0xB6: "movzx r32,[m+disp32]",
    0xB7: "movzx r32,[m+disp32]",
    0xBE: "movsx r32,[m+disp32]",
    0xBF: "movsx r32,[m+disp32]",
}
# 0xF0-0xF7 / 0xD0-0xD7 opcodes that write to memory (inc/dec/not/neg/test).
_MEM_RMW_OPS = {0xFE, 0xFF}  # inc/dec/not/neg/call/push via /r


def _modrm_reg(modrm: int) -> str:
    return _REGS[(modrm >> 3) & 7]


def _modrm_is_mem_disp32(modrm: int) -> bool:
    """True when the ModRM addresses memory with a 4-byte disp32 that directly
    follows the ModRM byte (no SIB byte between them):
      - mod=00, rm=101  -> absolute [disp32]
      - mod=10, rm!=100 -> [base+disp32] (rm=100 would insert a SIB byte)
    mod=11 is register-register (no memory operand at all)."""
    mod = (modrm >> 6) & 3
    rm = modrm & 7
    if mod == 0b11:
        return False
    if rm == 0b100:  # SIB present -> displacement is not immediately after ModRM
        return False
    if mod == 0b00:
        return rm == 0b101  # absolute disp32
    if mod == 0b10:
        return True  # base + disp32
    return False  # mod=01 -> disp8, never a 4-byte operand


def decode_reference_site(text: bytes, operand_pos: int, rva: int) -> dict:
    """Decode the instruction whose absolute operand starts at `operand_pos`
    inside the .text section bytes. `operand_pos` points at the first byte of
    the 4-byte little-endian address operand (offset into `text`).

    x86-32 layouts where the absolute dword is the final operand:
      2-byte opcode + ModRM:    [0F] [op] [modrm] [disp32]
      1-byte opcode + ModRM:    [op] [modrm] [disp32]
      moffs / imm forms:        [op] [imm32]        (A1/A3/B8+rd/68/…)
    """
    operand = struct.unpack_from("<I", text, operand_pos)[0]
    kind, width, reg, detail, opcode = "other", "dword", None, "", None

    def result() -> dict:
        return {
            "rva": f"0x{rva:08X}",
            "operand": f"0x{operand:08X}",
            "kind": kind,
            "width": width,
            "reg": reg,
            "opcode": (f"0x{opcode:02X}" if isinstance(opcode, int) and opcode < 0x100
                        else f"0x{opcode:04X}" if isinstance(opcode, int) else None),
            "detail": detail,
        }

    # Layout 1: two-byte opcode (0F xx) + ModRM disp32.
    if operand_pos >= 3 and text[operand_pos - 3] == 0x0F:
        op2 = text[operand_pos - 2]
        if op2 in _MODRM_OPS_2BYTE:
            opcode = 0x0F00 | op2
            kind = "load"
            width = "byte" if op2 in (0xB6, 0xBE) else "word"
            reg = _modrm_reg(text[operand_pos - 1])
            detail = _MODRM_OPS_2BYTE[op2]
            return result()
        # Unrecognized 0F-prefixed instruction: do not fall through and re-try
        # op2 as a one-byte opcode (that would misclassify 0F A1/A3/… forms).
        detail = f"unclassified 0F-prefixed opcode 0x{op2:02X}"
        return result()

    # Layout 2: one-byte opcode + ModRM disp32.
    if operand_pos >= 2:
        raw_op = text[operand_pos - 2]
        modrm = text[operand_pos - 1]
        if not _modrm_is_mem_disp32(modrm):
            detail = (f"ModRM 0x{modrm:02X} is not a mem disp32 form "
                      f"(op 0x{raw_op:02X})")
            return result()
        if raw_op in _MODRM_OPS:
            opcode = raw_op
            if raw_op in (0x88, 0x89, 0xC7):
                kind = "store"
            elif raw_op == 0x8D:
                kind = "lea"
            else:
                kind = "load"
            width = "byte" if raw_op in (0x88, 0x8A) else "dword"
            reg = _modrm_reg(modrm)
            detail = _MODRM_OPS[raw_op]
            return result()
        if raw_op in _MEM_RMW_OPS:
            opcode = raw_op
            reg = _modrm_reg(modrm)
            kind = "rmw" if reg in (0, 1, 2, 3) else "other"
            detail = (f"{_REGS[reg]}-group rmw on [abs]"
                      if reg in (0, 1, 2, 3) else f"{_REGS[reg]}-group on [abs]")
            return result()

    # Layout 3: opcode immediately before the operand (moffs / imm forms).
    if operand_pos >= 1:
        raw_op = text[operand_pos - 1]
        if raw_op in _MOFFS_OPS:
            opcode = raw_op
            kind = "store" if raw_op == 0xA3 else "load"
            reg = "eax"
            detail = _MOFFS_OPS[raw_op]
            return result()
        if 0xB8 <= raw_op <= 0xBF:
            opcode = raw_op
            kind = "imm"
            reg = _REGS[raw_op - 0xB8]
            detail = f"mov {reg},imm32"
            return result()
        if raw_op == 0x68:
            opcode = raw_op
            kind = "imm"
            detail = "push imm32"
            return result()
        detail = f"unclassified opcode 0x{raw_op:02X}"
    else:
        detail = "operand at section start (no preceding bytes)"
    return result()


def analyze_references(pe: PeImage, root: int) -> dict:
    """For every .text reference to `root`, decode the referencing instruction
    (store / load / lea / imm) — the offline equivalent of Find-what-writes."""
    result: dict = {"root": f"0x{root:08X}", "references": [], "summary": {}}
    text_sec = pe.text
    if text_sec is None:
        return result
    needle = struct.pack("<I", root)
    tstart = text_sec.raw_pointer
    tend = min(tstart + text_sec.raw_size, len(pe.data))
    text = pe.data[tstart:tend]
    pos = tstart
    while True:
        index = pe.data.find(needle, pos, tend)
        if index < 0:
            break
        operand_pos = index - tstart  # needle IS the 4-byte absolute operand
        rva = text_sec.virtual_address + operand_pos
        decoded = decode_reference_site(text, operand_pos, rva)
        result["references"].append(decoded)
        pos = index + 1
    summary: dict = {}
    for ref in result["references"]:
        key = ref["kind"]
        summary[key] = summary.get(key, 0) + 1
    result["summary"] = summary
    return result


# --------------------------------------------------------------------------- #
# Capability 5: plausible member-offset dump near a root (.data window)
# --------------------------------------------------------------------------- #

def dump_fields_near(pe: PeImage, root: int, window: int = 0x80) -> dict:
    """Scan a .data window around `root` and classify plausible typed values:
    float32 (position-like), doubles (time-like), in-module pointers (chain
    continuations), and small ints. Produces candidate member displacements
    (relative to the root) for a live session, never runtime offsets."""
    section = pe.section_of(root)
    result: dict = {
        "root": f"0x{root:08X}",
        "section": section,
        "window_bytes": window,
        "float32_candidates": [],
        "double_candidates": [],
        "pointer_candidates": [],
        "int32_candidates": [],
    }
    if section is None:
        return result
    raw_root = pe.rva_to_raw(root)
    if raw_root is None:
        return result
    # Clamp the window to the containing section's raw extent so neighbors are
    # real same-section bytes (file raw layout == virtual layout within a
    # section, so relative offsets computed from raw are the true displacements).
    sec = next((s for s in pe.sections if s.name == section), None)
    if sec is None:
        return result
    lo = max(sec.raw_pointer, raw_root - window)
    hi = min(sec.raw_pointer + sec.raw_size, raw_root + window, len(pe.data))

    def rel_off(off: int) -> int:
        return off - raw_root  # signed displacement relative to the root

    for off in range(lo, hi - 3, 4):
        value = struct.unpack_from("<I", pe.data, off)[0]
        rel = rel_off(off)
        # pointer candidate: in-module address
        if pe.is_in_module(value) and value not in (0,):
            section_of_target = pe.section_of(value - pe.image_base)
            result["pointer_candidates"].append({
                "relative_offset": rel,
                "relative_offset_hex": f"0x{rel & 0xFFFFFFFF:08X}",
                "points_to": f"0x{value:08X}",
                "target_section": section_of_target,
            })
        # float32 candidate: finite, non-trivial, and not an integral value
        # that the int32 pass already reports (avoids double-counting small ints)
        fval = struct.unpack_from("<f", pe.data, off)[0]
        is_integral_small = (
            _finite(fval) and abs(fval) < 100_000 and fval == float(int(fval)))
        if _finite(fval) and abs(fval) > 1e-6 and abs(fval) < 1e7 \
                and not is_integral_small:
            result["float32_candidates"].append({
                "relative_offset": rel,
                "relative_offset_hex": f"0x{rel & 0xFFFFFFFF:08X}",
                "value": round(fval, 4),
            })
        # int32 candidate: small non-negative
        if 0 < value < 100_000:
            result["int32_candidates"].append({
                "relative_offset": rel,
                "relative_offset_hex": f"0x{rel & 0xFFFFFFFF:08X}",
                "value": value,
            })
    for off in range(lo, hi - 7, 8):
        dval = struct.unpack_from("<d", pe.data, off)[0]
        rel = rel_off(off)
        if _finite(dval) and 0.0 < dval < 100_000.0:
            result["double_candidates"].append({
                "relative_offset": rel,
                "relative_offset_hex": f"0x{rel & 0xFFFFFFFF:08X}",
                "value": round(dval, 4),
            })
    return result


def _finite(value: float) -> bool:
    return value == value and abs(value) != float("inf")


# --------------------------------------------------------------------------- #
# Capability 6: record-array member mapping
# --------------------------------------------------------------------------- #


def _module_va_to_rva(pe: PeImage, value: int) -> Optional[int]:
    """Convert a module VA (image_base + rva) to its rva, or None."""
    if not pe.is_in_module(value):
        return None
    return value - pe.image_base


def _resolve_vtable_rtti_name(pe: PeImage, vtable_rva: int) -> Optional[str]:
    """MSVC x86 RTTI: vtable[-1] (at vtable-4) holds the Complete Object
    Locator; COL+12 holds the pTypeDescriptor; the TypeDescriptor holds its
    mangled name **inline** at td+8 (a char[] — NOT a pointer). Returns the
    class name string or None when the chain is incomplete."""
    col_va = pe.dword(vtable_rva - 4)
    if col_va is None or not pe.is_in_module(col_va):
        return None
    col_rva = _module_va_to_rva(pe, col_va)
    if col_rva is None:
        return None
    # COL signature must be 0 on x86 (sanity check before trusting the chain).
    if pe.dword(col_rva) != 0:
        return None
    td_va = pe.dword(col_rva + 12)
    if td_va is None or not pe.is_in_module(td_va):
        return None
    td_rva = _module_va_to_rva(pe, td_va)
    if td_rva is None:
        return None
    # td+0 = pVFTable (must be in module), td+4 = spare, td+8 = inline name.
    if pe.dword(td_rva) is None or not pe.is_in_module(pe.dword(td_rva)):
        return None
    raw = pe.rva_to_raw(td_rva + 8)
    if raw is None:
        return None
    end = pe.data.find(b"\0", raw, raw + 256)
    if end < 0:
        return None
    name = pe.data[raw:end].decode("latin1", "replace")
    # RTTI names are mangled (start with .?AV / .?AU); reject strings that
    # merely sit after a pointer-looking slot by requiring the mangled prefix.
    if not (name.startswith(".?AV") or name.startswith(".?AU")):
        return None
    return name


def discover_vtables(pe: PeImage, min_slots: int = 5, with_names: bool = True,
                     max_results: int = 200) -> dict:
    """Find vtable candidates in .rdata: runs of >= min_slots consecutive
    4-aligned dwords that all point into .text. Optionally resolve each
    vtable's RTTI class name via the COL chain, then find .data slots that
    point at each vtable base (named singleton roots)."""
    result: dict = {
        "scan": "vtables",
        "min_slots": min_slots,
        "vtable_count": 0,
        "named_count": 0,
        "vtables": [],
        "data_roots": [],
    }
    rdata = pe.rdata
    if rdata is None:
        return result
    start = rdata.raw_pointer
    end = min(start + rdata.raw_size, len(pe.data))
    text_sec = pe.text
    if text_sec is None:
        return result
    t_lo, t_hi = text_sec.virtual_address, text_sec.end

    # Pass 1: mark 4-aligned .rdata offsets whose dword is a .text VA.
    marked: list[int] = []
    for off in range(start, end - 3, 4):
        value = struct.unpack_from("<I", pe.data, off)[0]
        rva = value - pe.image_base
        if t_lo <= rva < t_hi:
            marked.append(off)

    # Pass 2: merge consecutive marks into runs.
    runs: list[tuple[int, int]] = []  # (raw_start, slot_count)
    run_start = None
    prev = None
    for off in marked:
        if run_start is None:
            run_start = off
        elif off != prev + 4:
            runs.append((run_start, (prev - run_start) // 4 + 1))
            run_start = off
        prev = off
    if run_start is not None and prev is not None:
        runs.append((run_start, (prev - run_start) // 4 + 1))

    vtable_bases: list[int] = []
    for raw_start, slot_count in runs:
        if slot_count < min_slots:
            continue
        vtable_rva = rdata.virtual_address + (raw_start - start)
        entry: dict = {
            "vtable_rva": f"0x{vtable_rva:08X}",
            "slots": slot_count,
        }
        if with_names:
            name = _resolve_vtable_rtti_name(pe, vtable_rva)
            entry["rtti_name"] = name
            if name:
                result["named_count"] += 1
        result["vtables"].append(entry)
        vtable_bases.append(vtable_rva)

    result["vtable_count"] = len(result["vtables"])
    result["vtables"] = result["vtables"][:max_results]

    # Pass 3: find .data slots pointing at the vtable bases.
    data_sec = pe.data_sec
    if data_sec is not None and vtable_bases:
        bases = {pe.image_base + rva for rva in vtable_bases}
        d_start = data_sec.raw_pointer
        d_end = min(d_start + data_sec.raw_size, len(pe.data))
        root_count = 0
        for off in range(d_start, d_end - 3, 4):
            value = struct.unpack_from("<I", pe.data, off)[0]
            if value in bases:
                slot_rva = data_sec.virtual_address + (off - d_start)
                result["data_roots"].append({
                    "slot_rva": f"0x{slot_rva:08X}",
                    "points_to_vtable": f"0x{value - pe.image_base:08X}",
                })
                root_count += 1
        result["data_root_count"] = root_count
    return result


def map_record_array(pe: PeImage, base: int, stride: int, count: int) -> dict:
    """Classify each member of a repeating record array (like the 0x50-byte
    EH/handler record family found around 0x03FA0C74). For each dword member
    of each record, classify as zero (runtime-initialized candidate),
    in-module pointer (chain continuation), small int, or float32 — and flag
    which members are consistently zero on disk (the runtime-written slots)."""
    section = pe.section_of(base)
    result: dict = {
        "base": f"0x{base:08X}",
        "section": section,
        "stride": f"0x{stride:X}",
        "record_count": count,
        "member_count": stride // 4,
        "records": [],
        "runtime_slots": [],
    }
    for record_index in range(count):
        rva = base + record_index * stride
        if pe.section_of(rva) != section:
            break
        raw = pe.rva_to_raw(rva)
        if raw is None or raw + stride > len(pe.data):
            break
        members = []
        for member_index in range(stride // 4):
            offset = member_index * 4
            value = struct.unpack_from("<I", pe.data, raw + offset)[0]
            kind = "opaque"
            note = ""
            if value == 0:
                kind = "zero"
            elif pe.is_in_module(value):
                kind = "pointer"
                note = f"-> {pe.section_of(value - pe.image_base)}"
            elif 0 < value < 100_000:
                kind = "int32"
            else:
                fval = struct.unpack_from("<f", pe.data, raw + offset)[0]
                if _finite(fval) and abs(fval) > 1e-6 and abs(fval) < 1e7 \
                        and not (abs(fval) < 100_000 and fval == float(int(fval))):
                    kind = "float32"
                    note = f"~{fval:.3f}"
            members.append({
                "member": member_index,
                "relative_offset": offset,
                "relative_offset_hex": f"0x{offset:02X}",
                "value": f"0x{value:08X}",
                "kind": kind,
                "note": note,
            })
        record = {
            "record_index": record_index,
            "rva": f"0x{rva:08X}",
            "members": members,
        }
        result["records"].append(record)

    # Runtime slots: members that are zero on disk for at least one record.
    # Report the zero-count so heterogeneous families (like the 0x50 EH
    # records, where early records zero a slot later records populate) stay
    # honest: a low zero-count member is a per-record runtime field, while a
    # high zero-count member is a family-wide runtime-initialized slot.
    if result["records"]:
        mapped = len(result["records"])
        member_count = stride // 4
        for member_index in range(member_count):
            zero_count = sum(
                1 for rec in result["records"]
                if rec["members"][member_index]["kind"] == "zero")
            if zero_count > 0:
                result["runtime_slots"].append({
                    "member": member_index,
                    "relative_offset": member_index * 4,
                    "relative_offset_hex": f"0x{member_index * 4:02X}",
                    "zero_count": zero_count,
                    "of_records": mapped,
                })
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
    parser.add_argument("--refs", metavar="RVA[,RVA...]",
                        help="decode every .text reference site for the given "
                             "roots (offline what-writes-this), comma-separated")
    parser.add_argument("--fields", metavar="RVA[,RVA...]",
                        help="dump plausible typed member candidates in a .data "
                             "window around the given roots, comma-separated")
    parser.add_argument("--window", type=int, default=0x80,
                        help=".data window bytes around each --fields root (default 128)")
    parser.add_argument("--record-map", metavar="BASE,STRIDE[,COUNT]",
                        help="classify each member of a repeating record array "
                             "(e.g. 0x03FA0C20,0x50,8) and list runtime-initialized "
                             "(zero-on-disk) slots")
    parser.add_argument("--vtables", action="store_true",
                        help="discover vtable candidates in .rdata (consecutive "
                             ".text-pointer runs), resolve RTTI names, and list "
                             ".data slots pointing at vtable bases")
    parser.add_argument("--min-slots", type=int, default=5,
                        help="minimum consecutive .text slots for --vtables (default 5)")
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

    if args.refs is not None:
        roots = []
        for raw in args.refs.split(","):
            raw = raw.strip()
            try:
                roots.append(int(raw, 0))
            except ValueError:
                LOG.error("invalid --refs RVA: %s", raw)
                return 2
        results["refs"] = []
        for root in roots:
            LOG.info("reference-site analysis 0x%08X", root)
            analysis = analyze_references(pe, root)
            results["refs"].append(analysis)
            LOG.info("  summary: %s", analysis["summary"])
            for ref in analysis["references"]:
                LOG.info("    %s %s %s reg=%s op=%s %s",
                         ref["rva"], ref["kind"], ref["detail"],
                         ref["reg"] or "-", ref["opcode"], ref["operand"])
        ran_any = True

    if args.fields is not None:
        roots = []
        for raw in args.fields.split(","):
            raw = raw.strip()
            try:
                roots.append(int(raw, 0))
            except ValueError:
                LOG.error("invalid --fields RVA: %s", raw)
                return 2
        results["fields"] = []
        for root in roots:
            LOG.info("field dump 0x%08X window=%d", root, args.window)
            fields = dump_fields_near(pe, root, args.window)
            results["fields"].append(fields)
            LOG.info("  float32=%d double=%d pointer=%d int32=%d",
                     len(fields["float32_candidates"]),
                     len(fields["double_candidates"]),
                     len(fields["pointer_candidates"]),
                     len(fields["int32_candidates"]))
            for candidate in fields["pointer_candidates"]:
                LOG.info("    ptr +%s -> %s (%s)",
                         candidate["relative_offset_hex"],
                         candidate["points_to"], candidate["target_section"])
            for candidate in fields["double_candidates"]:
                LOG.info("    dbl +%s = %s",
                         candidate["relative_offset_hex"], candidate["value"])
            for candidate in fields["float32_candidates"][:8]:
                LOG.info("    f32 +%s = %s",
                         candidate["relative_offset_hex"], candidate["value"])
            for candidate in fields["int32_candidates"][:8]:
                LOG.info("    i32 +%s = %s",
                         candidate["relative_offset_hex"], candidate["value"])
        ran_any = True

    if args.record_map is not None:
        parts = [part.strip() for part in args.record_map.split(",")]
        if len(parts) not in (2, 3):
            LOG.error("--record-map needs BASE,STRIDE[,COUNT]: %s", args.record_map)
            return 2
        try:
            base = int(parts[0], 0)
            stride = int(parts[1], 0)
            count = int(parts[2], 0) if len(parts) == 3 else 8
        except ValueError:
            LOG.error("invalid --record-map value: %s", args.record_map)
            return 2
        if stride <= 0 or stride % 4 != 0:
            LOG.error("--record-map stride must be a positive multiple of 4")
            return 2
        LOG.info("record-array map 0x%08X stride=0x%X count=%d", base, stride, count)
        mapping = map_record_array(pe, base, stride, count)
        results["record_map"] = mapping
        LOG.info("  section=%s records_mapped=%d runtime_slots=%d",
                 mapping["section"], len(mapping["records"]),
                 len(mapping["runtime_slots"]))
        for slot in mapping["runtime_slots"]:
            LOG.info("    runtime slot member=%d +%s",
                     slot["member"], slot["relative_offset_hex"])
        for record in mapping["records"][:4]:
            kinds = " ".join(f"{m['relative_offset_hex']}:{m['kind']}"
                             for m in record["members"])
            LOG.info("    rec[%d] %s %s", record["record_index"],
                     record["rva"], kinds)
        ran_any = True

    if args.vtables:
        LOG.info("vtable discovery: min slots %d", args.min_slots)
        vtable_result = discover_vtables(pe, min_slots=args.min_slots)
        results["vtables"] = vtable_result
        LOG.info("  %d vtables found (%d named), %d data roots",
                 vtable_result["vtable_count"], vtable_result["named_count"],
                 vtable_result.get("data_root_count", 0))
        named = [v for v in vtable_result["vtables"] if v.get("rtti_name")]
        for entry in named[:25]:
            LOG.info("    %s (%d slots) %s", entry["vtable_rva"],
                     entry["slots"], entry["rtti_name"])
        for root in vtable_result.get("data_roots", [])[:20]:
            LOG.info("    data root %s -> vtable %s", root["slot_rva"],
                     root["points_to_vtable"])
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
