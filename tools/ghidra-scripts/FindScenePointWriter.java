// FindScenePointWriter.java - locate the writer of the type-39 scene-point
// packet (28 bytes = 7 float32, per-frame ~60 Hz) via its bit-exact
// trailing constant.
//
// The type-39 payload observed on 11.19.0 replays (2026-08-10, both Oasis
// Palms and Dead Rail) is 7 float32. The last two floats are often
// (0.0, -0.0011081547) — the -0.0011081547 value is bit-exact stable as
// 0xBA913F80 across many packets, which makes it a strong static anchor.
// A function that (a) references this constant, (b) writes 7 consecutive
// floats, and (c) runs per-frame is the scene-point writer.
//
// This pass scans every defined memory byte for the constant's little-endian
// byte pattern, maps each hit to its enclosing function (for code hits),
// ranks functions by hit count, and cross-references the known camera
// primitives (FUN_00729570 = generic 4x4 matmul, FUN_00d29ea0 = global
// accessor) by searching each candidate's called-function set.
//
// Usage (analyzed project WotBlitz must already exist):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe \
//       -postScript FindScenePointWriter.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\scenepoint.log

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;

public class FindScenePointWriter extends GhidraScript {

    private static final byte[] PATTERN = {
        (byte) 0x80, (byte) 0x3F, (byte) 0x91, (byte) 0xBA   // f32 -0.0011081547 LE
    };

    // Known camera/scene primitives from prior passes (RVA -> role).
    private static final long MATMUL_RVA = 0x00729570L;      // FUN_00729570
    private static final long ACCESSOR_RVA = 0x00D29EA0L;    // FUN_00d29ea0(0)

    @Override
    public void run() throws Exception {
        String outPath = getEvidenceOutputPath("scenepoint-constants.txt");
        PrintWriter w = new PrintWriter(new File(outPath), "UTF-8");
        Address imageBase = currentProgram.getImageBase();
        w.println("=== FindScenePointWriter: pattern "
                  + "80 3F 91 BA (f32 -0.0011081547) ===");
        w.println("program=" + currentProgram.getName() + " image_base=" + imageBase);

        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();

        Map<Long, List<String>> byFunction = new HashMap<Long, List<String>>();
        List<String> dataHits = new ArrayList<String>();
        int totalHits = 0;

        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isInitialized() || block.getSize() < PATTERN.length) {
                continue;
            }
            Address start = block.getStart();
            long size = block.getSize();
            byte[] buf = new byte[(int) Math.min(size, 0x400000L)];
            long offset = 0;
            while (offset < size) {
                int n = (int) Math.min(buf.length, size - offset);
                block.getBytes(start.add(offset), buf, 0, n);
                for (int i = 0; i + PATTERN.length <= n; i++) {
                    boolean match = true;
                    for (int k = 0; k < PATTERN.length; k++) {
                        if (buf[i + k] != PATTERN[k]) {
                            match = false;
                            break;
                        }
                    }
                    if (!match) {
                        continue;
                    }
                    totalHits++;
                    Address addr = start.add(offset + i);
                    Function fn = listing.getFunctionContaining(addr);
                    if (fn != null) {
                        long rva = addr.getOffset() - imageBase.getOffset();
                        List<String> list = byFunction.get(rva);
                        if (list == null) {
                            list = new ArrayList<String>();
                            byFunction.put(rva, list);
                        }
                        list.add(String.format("  0x%08X @ %s + 0x%X",
                                rva, fn.getName(), addr.getOffset() - fn.getEntryPoint().getOffset()));
                    } else {
                        dataHits.add(String.format("  0x%08X (%s + 0x%X) [data]",
                                addr.getOffset() - imageBase.getOffset(),
                                block.getName(),
                                addr.getOffset() - block.getStart().getOffset()));
                    }
                }
                offset += n;
            }
        }

        w.println("total hits: " + totalHits);
        w.println("data-region hits: " + dataHits.size());
        for (String d : dataHits) {
            w.println(d);
        }

        // Rank code functions by hit count.
        List<Map.Entry<Long, List<String>>> ranked = new ArrayList<Map.Entry<Long, List<String>>>(byFunction.entrySet());
        Collections.sort(ranked, new Comparator<Map.Entry<Long, List<String>>>() {
            public int compare(Map.Entry<Long, List<String>> a, Map.Entry<Long, List<String>> b) {
                return Integer.compare(b.getValue().size(), a.getValue().size());
            }
        });

        w.println("");
        w.println("code functions referencing the constant: " + ranked.size());
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        int shown = 0;
        for (Map.Entry<Long, List<String>> e : ranked) {
            Address entry = imageBase.add(e.getKey());
            Function fn = listing.getFunctionAt(entry);
            if (fn == null) {
                continue;
            }
            Set<Long> calls = calledRvas(fn, imageBase);
            boolean callsMatmul = calls.contains(MATMUL_RVA);
            boolean callsAccessor = calls.contains(ACCESSOR_RVA);
            w.println("");
            w.println("### RVA 0x" + Long.toHexString(e.getKey())
                      + " " + fn.getName() + " hits=" + e.getValue().size()
                      + " callsMatmul=" + callsMatmul
                      + " callsAccessor=" + callsAccessor);
            for (String h : e.getValue()) {
                w.println(h);
            }
            // Decompile the top 6 candidates that touch the camera primitives
            // first, else the top 3.
            if (callsMatmul || callsAccessor) {
                if (shown < 8) {
                    decompile(w, decomp, fn, e.getKey());
                    shown++;
                }
            } else if (shown < 3 && e.getValue().size() >= 2) {
                decompile(w, decomp, fn, e.getKey());
                shown++;
            }
            if (shown >= 8) {
                break;
            }
        }
        w.close();
        println("Wrote " + outPath + " hits=" + totalHits + " funcs=" + ranked.size());
    }

    private Set<Long> calledRvas(Function fn, Address imageBase) {
        Set<Long> out = new LinkedHashSet<Long>();
        for (Instruction insn : currentProgram.getListing().getInstructions(fn.getBody(), true)) {
            for (Reference r : insn.getReferencesFrom()) {
                if (r.getReferenceType().isCall()) {
                    out.add(r.getToAddress().getOffset() - imageBase.getOffset());
                }
            }
        }
        return out;
    }

    private void decompile(PrintWriter w, DecompInterface decomp, Function fn, long rva) {
        ghidra.app.decompiler.DecompileResults res = decomp.decompileFunction(fn, 30, monitor);
        if (res != null && res.decompileCompleted()) {
            String c = res.getDecompiledFunction().getC();
            if (c != null) {
                w.println("  --- decompile ---");
                w.println(c);
            }
        }
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"), ".build\\ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs()) {
            throw new IllegalStateException("Could not create Ghidra evidence directory");
        }
        return new File(directory, fileName).getAbsolutePath();
    }
}
