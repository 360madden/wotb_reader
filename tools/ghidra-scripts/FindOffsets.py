# FindOffsets.py — Ghidra headless script for WoT Blitz offset discovery
# v3: fixes all API usage bugs identified by code review
#
# Usage:
#   1) Import + full analysis (one-time, ~10-15 min for 71MB):
#      set JAVA_HOME=C:\Program Files\Eclipse Adoptium\jdk-21.0.11.10-hotspot
#      analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
#          -import C:\Games\World_of_Tanks_Blitz\wotblitz.exe
#
#   2) Run this script on the analyzed project:
#      analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
#          -process wotblitz.exe \
#          -postScript FindOffsets.py \
#          -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
#          -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\ghidra-analysis.log
#
# Searches for known game state strings, traces their cross-references,
# and outputs candidate struct offsets to a JSON file.

from __future__ import print_function
import json
import sys
import os

OUTPUT_DIR = r"C:\work\wotb_reader\tools\ghidra-scripts"
OUTPUT_FILE = os.path.join(OUTPUT_DIR, "ghidra-offset-candidates.json")

SEARCH_TERMS = [
    ("health",       "playerHP",          "int32"),
    ("hitpoints",    "playerHP",          "int32"),
    ("hp",           "playerHP",          "int32"),
    ("position",     "playerPositionX",   "float[3]"),
    ("xpos",         "playerPositionX",   "float"),
    ("ypos",         "playerPositionY",   "float"),
    ("zpos",         "playerPositionZ",   "float"),
    ("replayTime",   "replayTime",        "double"),
    ("replay_time",  "replayTime",        "double"),
    ("yaw",          "playerYaw",         "float"),
    ("pitch",        "cameraPitch",       "float"),
    ("cameraPitch",  "cameraPitch",       "float"),
    ("alive",        "aliveTankCount",    "int32"),
    ("tanksAlive",   "aliveTankCount",    "int32"),
]


def is_string(val):
    """Safe string check for Jython (Python 2) and Python 3."""
    try:
        return isinstance(val, basestring)
    except NameError:
        return isinstance(val, str)


def find_strings(program, monitor):
    """
    Use Ghidra's SearchProgramUtilities to find matching strings.
    Returns dict: { search_term: [Address, ...] }
    """
    from ghidra.app.util import SearchProgramUtilities
    from ghidra.app.util.query import SearchData

    results = {}
    for pattern, field_name, _ in SEARCH_TERMS:
        if monitor.isCancelled():
            break
        monitor.setMessage("Searching: " + pattern)
        print("  Searching for '%s'..." % pattern)

        addrs = []
        try:
            # SearchData(pattern, caseSensitive) then searchData(program, searchData, monitor)
            search_data = SearchData(pattern, False)
            iterator = SearchProgramUtilities.searchData(program, search_data, monitor)

            while iterator.hasNext() and not monitor.isCancelled():
                addr = iterator.next()
                addrs.append(addr)

            print("    Found %d match(es)" % len(addrs))
        except Exception as e:
            print("    FAILED: %s" % str(e))
            # Print stack trace for debugging
            import traceback
            traceback.print_exc()

        results[pattern] = addrs
    return results


def trace_xrefs(program, string_addrs, monitor):
    """
    For each string address found, find all code cross-references
    and record the referencing function and data offsets used.
    """
    ref_mgr = program.getReferenceManager()
    func_mgr = program.getFunctionManager()
    memory = program.getMemory()

    xrefs = {}

    for pattern, field_name, field_type in SEARCH_TERMS:
        if monitor.isCancelled():
            break

        addrs = string_addrs.get(pattern, [])
        monitor.setMessage("Tracing xrefs: " + field_name)

        field_xrefs = []
        seen_functions = set()

        for addr in addrs:
            if monitor.isCancelled():
                break

            refs = ref_mgr.getReferencesTo(addr)
            for ref in refs:
                from_addr = ref.getFromAddress()
                func = func_mgr.getFunctionContaining(from_addr)
                func_name = func.getName() if func else "???"

                func_key = str(from_addr) + ":" + func_name
                if func_key in seen_functions:
                    continue
                seen_functions.add(func_key)

                entry = {
                    "string_addr": str(addr),
                    "ref_addr": str(from_addr),
                    "function": str(func_name),
                    "data_refs": [],
                }

                if func is not None:
                    body = func.getBody()
                    listing = program.getListing()
                    code_units = listing.getCodeUnits(body, True)
                    data_ref_count = 0
                    for cu in code_units:
                        if data_ref_count >= 50:
                            break
                        try:
                            cu_refs = cu.getReferencesFrom()
                            for cu_ref in cu_refs:
                                to_addr = cu_ref.getToAddress()
                                if to_addr.getAddressSpace().isExternalSpace():
                                    continue
                                block = memory.getBlock(to_addr)
                                if block is not None and not block.isExecute():
                                    offset_val = to_addr.getOffset()
                                    if offset_val > 0:
                                        entry["data_refs"].append({
                                            "offset": offset_val,
                                            "mnemonic": str(cu.getMnemonicString()),
                                            "inst_addr": str(cu.getAddress()),
                                        })
                                        data_ref_count += 1
                        except Exception:
                            pass

                field_xrefs.append(entry)

        xrefs[field_name] = {
            "search_term": pattern,
            "field_type": field_type,
            "string_matches": len(addrs),
            "xrefs": field_xrefs,
        }

    return xrefs


def extract_candidates(xrefs, monitor):
    """Analyze cross-references to find most-referenced data offsets."""
    candidates = {}
    for field_name, data in xrefs.items():
        if monitor.isCancelled():
            break
        monitor.setMessage("Analyzing: " + field_name)

        offset_counts = {}
        for xref in data.get("xrefs", []):
            for dr in xref.get("data_refs", []):
                off = dr.get("offset", 0)
                if off > 0 and off < 0x7FFFFFFFF:
                    if off not in offset_counts:
                        offset_counts[off] = {"count": 0, "instructions": []}
                    offset_counts[off]["count"] += 1
                    if len(offset_counts[off]["instructions"]) < 5:
                        offset_counts[off]["instructions"].append({
                            "inst_addr": dr.get("inst_addr", ""),
                            "mnemonic": dr.get("mnemonic", ""),
                        })

        sorted_offsets = sorted(
            [{"offset": k, "count": v["count"], "instructions": v["instructions"]}
             for k, v in offset_counts.items()],
            key=lambda x: -x["count"]
        )[:20]

        functions = list(set(
            x.get("function", "") for x in data.get("xrefs", [])
        ))[:10]

        candidates[field_name] = {
            "search_term": data["search_term"],
            "field_type": data["field_type"],
            "string_matches": data["string_matches"],
            "xref_count": len(data.get("xrefs", [])),
            "referencing_functions": functions,
            "top_candidate_offsets": sorted_offsets[:10],
        }

    return candidates


def write_output(output_path, program_info, candidates):
    """Write discovery results as JSON."""
    output = {
        "program": program_info,
        "candidates": candidates,
        "summary": {
            "fields_with_strings": sum(1 for c in candidates.values() if c.get("string_matches", 0) > 0),
            "fields_with_xrefs": sum(1 for c in candidates.values() if c.get("xref_count", 0) > 0),
            "fields_with_offset_hints": sum(1 for c in candidates.values() if len(c.get("top_candidate_offsets", [])) > 0),
        },
    }

    with open(output_path, "w") as f:
        json.dump(output, f, indent=2)

    # Print summary
    print("\n" + "=" * 60)
    print("RESULTS")
    print("=" * 60)
    pi = program_info
    print("Program: %s v%s" % (pi.get("name", "?"), pi.get("version", "?")))
    print("SHA-256: %s" % pi.get("sha256", "?")[:16] + "...")
    print("Fields w/ strings: %d" % output["summary"]["fields_with_strings"])
    print("Fields w/ xrefs:   %d" % output["summary"]["fields_with_xrefs"])
    print("Fields w/ offsets: %d" % output["summary"]["fields_with_offset_hints"])
    print("Output: %s" % output_path)
    print()

    for field_name, c in sorted(candidates.items()):
        sm = c.get("string_matches", 0)
        xc = c.get("xref_count", 0)
        top = c.get("top_candidate_offsets", [])
        status = "✓" if top else " "
        print("  %s %-20s %d strings, %d xrefs" % (status, field_name, sm, xc))
        for func in c.get("referencing_functions", [])[:2]:
            print("    └─ func: %s" % func)
        for off in top[:3]:
            print("    └─ off: 0x%08X  (%d refs)" % (off.get("offset", 0), off.get("count", 0)))


def main():
    print("=" * 60)
    print("FindOffsets.py v3 — WoT Blitz Offset Discovery")
    print("=" * 60)

    program = getCurrentProgram()
    if program is None:
        print("ERROR: No program loaded. Use -import <wotblitz.exe> first.")
        sys.exit(1)

    monitor = getMonitor()

    info = {
        "name": program.getName(),
        "version": program.getExecutableVersion(),
        "sha256": program.getExecutableSHA256(),
        "language": str(program.getLanguageID()),
    }
    print("Program: %s" % info["name"])
    print("Version: %s" % info["version"])
    print("SHA-256: %s" % info["sha256"])
    print()

    # Step 1
    print("[1/3] Searching for known strings...")
    string_results = find_strings(program, monitor)
    print()

    # Step 2
    print("[2/3] Tracing cross-references...")
    xref_results = trace_xrefs(program, string_results, monitor)
    print()

    # Step 3
    print("[3/3] Extracting offset candidates...")
    candidates = extract_candidates(xref_results, monitor)

    # Ensure output directory exists
    try:
        os.makedirs(OUTPUT_DIR, exist_ok=True)
    except Exception:
        pass

    try:
        write_output(OUTPUT_FILE, info, candidates)
        print("\nDone. Results at: %s" % OUTPUT_FILE)
    except Exception as e:
        print("\nERROR writing output: %s" % str(e))
        import traceback
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
