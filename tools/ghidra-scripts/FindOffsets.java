// FindOffsets.java — WoT Blitz offset discovery (Ghidra headless)
// Port of FindOffsets.py v3 to Java because PyGhidra is not available.
//
// Usage: analyzeHeadless.bat <projectDir> WotBlitz
//           -process wotblitz.exe
//           -postScript FindOffsets.java
//           -scriptPath <this-dir>
//
// Searches for game-state strings, traces cross-references, and
// outputs candidate struct offsets to ghidra-offset-candidates.json.

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSetView;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;

import java.io.FileWriter;
import java.io.IOException;
import java.time.Instant;
import java.util.*;

public class FindOffsets extends GhidraScript {

    private static final String OUTPUT_PATH =
        "C:\\work\\wotb_reader\\tools\\ghidra-scripts\\ghidra-offset-candidates.json";

    private static final String[][] SEARCH_TERMS = {
        {"health",      "playerHP",         "int32"},
        {"hitpoints",   "playerHP",         "int32"},
        {"hp",          "playerHP",         "int32"},
        {"position",    "playerPositionX",  "float[3]"},
        {"xpos",        "playerPositionX",  "float"},
        {"ypos",        "playerPositionY",  "float"},
        {"zpos",        "playerPositionZ",  "float"},
        {"replayTime",  "replayTime",       "double"},
        {"replay_time", "replayTime",       "double"},
        {"yaw",         "playerYaw",        "float"},
        {"pitch",       "cameraPitch",      "float"},
        {"cameraPitch", "cameraPitch",      "float"},
        {"alive",       "aliveTankCount",   "int32"},
        {"tanksAlive",  "aliveTankCount",   "int32"},
    };

    private static final String SEP = "============================================================";

    @Override
    protected void run() throws Exception {
        log(SEP);
        log("FindOffsets.java — WoT Blitz Offset Discovery");
        log(SEP);
        log("Program: " + currentProgram.getName());
        log("Language: " + currentProgram.getLanguageID());
        log("");

        // ── Step 1: Search for strings ──
        log("[1/3] Searching for known strings...");
        Map<String, List<Address>> stringResults = findStrings();
        log("");

        // ── Step 2: Trace cross-references ──
        log("[2/3] Tracing cross-references...");
        Map<String, Map<String, Object>> xrefResults = traceXrefs(stringResults);
        log("");

        // ── Step 3: Extract offset candidates ──
        log("[3/3] Extracting offset candidates...");
        Map<String, Map<String, Object>> candidates = extractCandidates(xrefResults);

        // ── Write output ──
        writeOutput(candidates);
        log("");
        log("Done. Results at: " + OUTPUT_PATH);
    }

    private void log(String msg) {
        String ts = Instant.now().toString().replace("T", " ").substring(0, 23);
        println("[" + ts + "] " + msg);
    }

    // ── Step 1: String search ──
    // Uses direct memory scanning because SearchProgramUtilities is not
    // available in Ghidra's Java scripting API (PyGhidra-only).

    private Map<String, List<Address>> findStrings() throws Exception {
        Map<String, List<Address>> results = new LinkedHashMap<>();
        Memory memory = currentProgram.getMemory();

        // Pre-compute byte patterns for each search term
        for (String[] entry : SEARCH_TERMS) {
            String pattern = entry[0];
            if (monitor.isCancelled()) break;

            log("  Searching for '" + pattern + "'...");
            List<Address> addrs = new ArrayList<>();
            byte[] patternBytes = pattern.getBytes("ASCII");

            try {
                for (MemoryBlock block : memory.getBlocks()) {
                    if (monitor.isCancelled()) break;
                    if (!block.isInitialized() || block.isExternalBlock()) continue;

                    byte[] blockBytes = new byte[(int) Math.min(block.getSize(), Integer.MAX_VALUE - 1)];
                    int bytesRead = 0;
                    try {
                        bytesRead = block.getBytes(block.getStart(), blockBytes);
                    } catch (Exception ex) {
                        continue; // skip unreadable blocks
                    }

                    for (int i = 0; i <= bytesRead - patternBytes.length; i++) {
                        if (monitor.isCancelled()) break;
                        boolean match = true;
                        for (int j = 0; j < patternBytes.length; j++) {
                            byte b1 = blockBytes[i + j];
                            byte b2 = patternBytes[j];
                            // Case-insensitive ASCII comparison
                            if (b1 != b2
                                && (b1 < 'A' || b1 > 'z' || b2 < 'A' || b2 > 'z'
                                    || (b1 - b2) != 32 && (b2 - b1) != 32))
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match) {
                            addrs.add(block.getStart().add(i));
                            if (addrs.size() >= 5000) break; // safety limit
                        }
                    }
                    if (addrs.size() >= 5000) break;
                }
                log("    Found " + addrs.size() + " match(es)");
            } catch (Exception e) {
                log("    FAILED: " + e.getMessage());
            }

            results.put(pattern, addrs);
        }
        return results;
    }

    // ── Step 2: Cross-reference tracing ──

    private Map<String, Map<String, Object>> traceXrefs(
            Map<String, List<Address>> stringAddrs) {

        ReferenceManager refMgr = currentProgram.getReferenceManager();
        FunctionManager funcMgr = currentProgram.getFunctionManager();
        Map<String, Map<String, Object>> xrefs = new LinkedHashMap<>();

        for (String[] entry : SEARCH_TERMS) {
            String pattern = entry[0];
            String fieldName = entry[1];
            String fieldType = entry[2];
            if (monitor.isCancelled()) break;

            List<Address> addrs = stringAddrs.getOrDefault(pattern, Collections.emptyList());
            log("  Tracing xrefs for " + fieldName + " (" + addrs.size() + " strings)...");

            List<Map<String, Object>> fieldXrefs = new ArrayList<>();
            Set<String> seenFunctions = new HashSet<>();

            int totalDataRefs = 0;
            for (Address addr : addrs) {
                if (monitor.isCancelled()) break;

                for (Reference ref : refMgr.getReferencesTo(addr)) {
                    Address fromAddr = ref.getFromAddress();
                    Function func = funcMgr.getFunctionContaining(fromAddr);
                    String funcName = func != null ? func.getName() : "???";

                    String funcKey = fromAddr + ":" + funcName;
                    if (seenFunctions.contains(funcKey)) continue;
                    seenFunctions.add(funcKey);

                    Map<String, Object> xrefEntry = new LinkedHashMap<>();
                    xrefEntry.put("string_addr", addr.toString());
                    xrefEntry.put("ref_addr", fromAddr.toString());
                    xrefEntry.put("function", funcName);

                    List<Map<String, Object>> dataRefs = new ArrayList<>();
                    if (func != null) {
                        AddressSetView body = func.getBody();
                        Listing listing = currentProgram.getListing();
                        var codeUnits = listing.getCodeUnits(body, true);
                        int count = 0;
                        while (codeUnits.hasNext() && count < 50) {
                            CodeUnit cu = codeUnits.next();
                            try {
                                for (Reference cuRef : cu.getReferencesFrom()) {
                                    Address toAddr = cuRef.getToAddress();
                                    if (toAddr.getAddressSpace().isExternalSpace()) continue;
                                    MemoryBlock block = currentProgram.getMemory().getBlock(toAddr);
                                    if (block != null && !block.isExecute()) {
                                        long off = toAddr.getOffset();
                                        if (off > 0) {
                                            Map<String, Object> dr = new LinkedHashMap<>();
                                            dr.put("offset", off);
                                            dr.put("mnemonic", cu.getMnemonicString());
                                            dr.put("inst_addr", cu.getAddress().toString());
                                            dataRefs.add(dr);
                                            count++;
                                            totalDataRefs++;
                                        }
                                    }
                                }
                            } catch (Exception ignored) {
                                // Skip problematic code units
                            }
                        }
                    }
                    xrefEntry.put("data_refs", dataRefs);
                    fieldXrefs.add(xrefEntry);
                }
            }

            Map<String, Object> fieldData = new LinkedHashMap<>();
            fieldData.put("search_term", pattern);
            fieldData.put("field_type", fieldType);
            fieldData.put("string_matches", addrs.size());
            fieldData.put("xref_count", fieldXrefs.size());
            fieldData.put("total_data_refs", totalDataRefs);
            fieldData.put("xrefs", fieldXrefs);
            xrefs.put(fieldName, fieldData);

            log("    " + fieldXrefs.size() + " unique xrefs, " + totalDataRefs + " data refs");
        }
        return xrefs;
    }

    // ── Step 3: Candidate extraction ──

    private Map<String, Map<String, Object>> extractCandidates(
            Map<String, Map<String, Object>> xrefs) {

        Map<String, Map<String, Object>> candidates = new LinkedHashMap<>();

        for (Map.Entry<String, Map<String, Object>> entry : xrefs.entrySet()) {
            String fieldName = entry.getKey();
            Map<String, Object> data = entry.getValue();
            if (monitor.isCancelled()) break;

            @SuppressWarnings("unchecked")
            List<Map<String, Object>> fieldXrefs =
                (List<Map<String, Object>>) data.getOrDefault("xrefs", Collections.emptyList());

            // Count offset occurrences
            Map<Long, Map<String, Object>> offsetCounts = new LinkedHashMap<>();
            for (Map<String, Object> xref : fieldXrefs) {
                @SuppressWarnings("unchecked")
                List<Map<String, Object>> dataRefs =
                    (List<Map<String, Object>>) xref.getOrDefault("data_refs", Collections.emptyList());

                for (Map<String, Object> dr : dataRefs) {
                    long off = ((Number) dr.get("offset")).longValue();
                    if (off <= 0 || off >= 0x7FFFFFFFL) continue;

                    offsetCounts.computeIfAbsent(off, k -> {
                        Map<String, Object> m = new LinkedHashMap<>();
                        m.put("count", 0);
                        m.put("instructions", new ArrayList<Map<String, Object>>());
                        return m;
                    });

                    @SuppressWarnings("unchecked")
                    Map<String, Object> oc = offsetCounts.get(off);
                    oc.put("count", ((Number) oc.get("count")).intValue() + 1);

                    @SuppressWarnings("unchecked")
                    List<Map<String, Object>> insts =
                        (List<Map<String, Object>>) oc.get("instructions");
                    if (insts.size() < 5) {
                        Map<String, Object> inst = new LinkedHashMap<>();
                        inst.put("inst_addr", dr.getOrDefault("inst_addr", ""));
                        inst.put("mnemonic", dr.getOrDefault("mnemonic", ""));
                        insts.add(inst);
                    }
                }
            }

            // Sort by count descending, take top 20
            List<Map<String, Object>> sortedOffsets = new ArrayList<>();
            for (Map.Entry<Long, Map<String, Object>> oc : offsetCounts.entrySet()) {
                Map<String, Object> item = new LinkedHashMap<>();
                item.put("offset", oc.getKey());
                item.put("count", oc.getValue().get("count"));
                item.put("instructions", oc.getValue().get("instructions"));
                sortedOffsets.add(item);
            }
            sortedOffsets.sort((a, b) ->
                Integer.compare(
                    ((Number) b.get("count")).intValue(),
                    ((Number) a.get("count")).intValue()));

            if (sortedOffsets.size() > 20) {
                sortedOffsets = sortedOffsets.subList(0, 20);
            }

            // Collect unique function names
            Set<String> funcs = new LinkedHashSet<>();
            for (Map<String, Object> xref : fieldXrefs) {
                funcs.add((String) xref.getOrDefault("function", ""));
                if (funcs.size() >= 10) break;
            }

            Map<String, Object> candidate = new LinkedHashMap<>();
            candidate.put("search_term", data.get("search_term"));
            candidate.put("field_type", data.get("field_type"));
            candidate.put("string_matches", data.get("string_matches"));
            candidate.put("xref_count", data.get("xref_count"));
            candidate.put("total_data_refs", data.get("total_data_refs"));
            candidate.put("referencing_functions", new ArrayList<>(funcs));
            candidate.put("top_candidate_offsets", sortedOffsets.subList(0, Math.min(10, sortedOffsets.size())));
            candidates.put(fieldName, candidate);
        }
        return candidates;
    }

    // ── JSON output ──

    private void writeOutput(Map<String, Map<String, Object>> candidates) {
        int fieldsWithStrings = 0, fieldsWithXrefs = 0, fieldsWithOffsets = 0;
        for (Map<String, Object> c : candidates.values()) {
            if (((Number) c.getOrDefault("string_matches", 0)).intValue() > 0) fieldsWithStrings++;
            if (((Number) c.getOrDefault("xref_count", 0)).intValue() > 0) fieldsWithXrefs++;
            var top = c.get("top_candidate_offsets");
            if (top instanceof List && !((List<?>) top).isEmpty()) fieldsWithOffsets++;
        }

        StringBuilder json = new StringBuilder();
        json.append("{\n");
        json.append("  \"program\": {\n");
        json.append("    \"name\": \"").append(jsonEscape(currentProgram.getName())).append("\",\n");
        json.append("    \"language\": \"").append(jsonEscape(currentProgram.getLanguageID().toString())).append("\",\n");
        json.append("    \"scanned_at_utc\": \"").append(Instant.now()).append("\"\n");
        json.append("  },\n");
        json.append("  \"summary\": {\n");
        json.append("    \"fields_with_strings\": ").append(fieldsWithStrings).append(",\n");
        json.append("    \"fields_with_xrefs\": ").append(fieldsWithXrefs).append(",\n");
        json.append("    \"fields_with_offset_hints\": ").append(fieldsWithOffsets).append("\n");
        json.append("  },\n");
        json.append("  \"candidates\": {\n");

        boolean first = true;
        for (Map.Entry<String, Map<String, Object>> e : candidates.entrySet()) {
            if (!first) json.append(",\n");
            first = false;

            Map<String, Object> c = e.getValue();
            json.append("    \"").append(jsonEscape(e.getKey())).append("\": {\n");
            json.append("      \"search_term\": \"").append(jsonEscape((String) c.get("search_term"))).append("\",\n");
            json.append("      \"field_type\": \"").append(jsonEscape((String) c.get("field_type"))).append("\",\n");
            json.append("      \"string_matches\": ").append(c.get("string_matches")).append(",\n");
            json.append("      \"xref_count\": ").append(c.get("xref_count")).append(",\n");
            json.append("      \"total_data_refs\": ").append(c.getOrDefault("total_data_refs", 0)).append(",\n");

            // referencing_functions
            @SuppressWarnings("unchecked")
            List<String> funcs = (List<String>) c.getOrDefault("referencing_functions", Collections.emptyList());
            json.append("      \"referencing_functions\": [");
            for (int i = 0; i < funcs.size(); i++) {
                if (i > 0) json.append(", ");
                json.append("\"").append(jsonEscape(funcs.get(i))).append("\"");
            }
            json.append("],\n");

            // top_candidate_offsets
            @SuppressWarnings("unchecked")
            List<Map<String, Object>> tops = (List<Map<String, Object>>)
                c.getOrDefault("top_candidate_offsets", Collections.emptyList());
            json.append("      \"top_candidate_offsets\": [");
            for (int i = 0; i < tops.size(); i++) {
                if (i > 0) json.append(", ");
                Map<String, Object> off = tops.get(i);
                json.append("{\"offset\": ").append(off.get("offset"))
                    .append(", \"count\": ").append(off.get("count")).append("}");
            }
            json.append("]\n");
            json.append("    }");
        }

        json.append("\n  }\n}\n");

        try (FileWriter fw = new FileWriter(OUTPUT_PATH)) {
            fw.write(json.toString());
        } catch (IOException ex) {
            log("ERROR writing output: " + ex.getMessage());
            return;
        }

        // Print summary
        log(SEP);
        log("RESULTS SUMMARY");
        log(SEP);
        log("Fields w/ strings: " + fieldsWithStrings);
        log("Fields w/ xrefs:   " + fieldsWithXrefs);
        log("Fields w/ offsets: " + fieldsWithOffsets);

        for (Map.Entry<String, Map<String, Object>> e : candidates.entrySet()) {
            Map<String, Object> c = e.getValue();
            int sm = ((Number) c.getOrDefault("string_matches", 0)).intValue();
            int xc = ((Number) c.getOrDefault("xref_count", 0)).intValue();
            @SuppressWarnings("unchecked")
            List<Map<String, Object>> top = (List<Map<String, Object>>)
                c.getOrDefault("top_candidate_offsets", Collections.emptyList());
            String status = top.isEmpty() ? " " : "+";
            log(String.format("  %s %-20s %d strings, %d xrefs",
                status, e.getKey(), sm, xc));
            for (Object f : (List<?>) c.getOrDefault("referencing_functions", Collections.emptyList())) {
                log("    func: " + f);
                break; // just first one
            }
            for (Map<String, Object> off : top.subList(0, Math.min(3, top.size()))) {
                log(String.format("    off: 0x%08X  (%d refs)",
                    ((Number) off.get("offset")).longValue(),
                    ((Number) off.get("count")).intValue()));
            }
        }
    }

    private static String jsonEscape(String s) {
        if (s == null) return "";
        return s.replace("\\", "\\\\").replace("\"", "\\\"");
    }
}
