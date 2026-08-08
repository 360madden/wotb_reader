// DumpCallers.java - walk FUN_00bb9b30's caller chain (entity-array container)
// to a stable global root for the FRESH43 candidate layout
// [entity+0x3C] + 0x1C/0x20/0x24 (position triple) / +0x60 (world matrix).
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpCallers.java 0x7B9B30 3 5 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\dump-callers.log
//
// Args: <start RVA> [maxDepth=3] [maxCallersPerNode=5]
// Prints per function: entry/body, first 80 instrs (data refs marked
// <<< GLOBAL), a full-body scan of references into non-executable memory
// (stable global-root candidates with read/write kind), decompile, callers.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.TreeSet;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

public class DumpCallers extends GhidraScript {

    private static final class Node {
        final long rva;
        final Function func;
        final int depth;
        Node(long rva, Function func, int depth) {
            this.rva = rva;
            this.func = func;
            this.depth = depth;
        }
    }

    @Override
    public void run() throws Exception {
        List<String> args = new ArrayList<String>();
        for (String a : getScriptArgs()) {
            args.add(a.trim());
        }
        if (args.isEmpty()) {
            println("ERROR: no RVA argument provided");
            return;
        }
        long startRva = Long.decode(args.get(0));
        int maxDepth = 3;
        int maxCallers = 5;
        // positional: args[1] = maxDepth, args[2] = maxCallersPerNode
        if (args.size() > 1) {
            try {
                maxDepth = Integer.parseInt(args.get(1));
            } catch (NumberFormatException e) {
                // ignore
            }
        }
        if (args.size() > 2) {
            try {
                maxCallers = Integer.parseInt(args.get(2));
            } catch (NumberFormatException e) {
                // ignore
            }
        }
        println("start RVA 0x" + Long.toHexString(startRva) +
                " maxDepth=" + maxDepth + " maxCallersPerNode=" + maxCallers);

        String outPath = getEvidenceOutputPath("callers-disasm.txt");
        PrintWriter w = new PrintWriter(new File(outPath));

        Address imageBase = currentProgram.getImageBase();
        w.println("=== program: " + currentProgram.getName() +
                  " image_base=" + imageBase + " ===");

        w.println("--- memory blocks ---");
        MemoryBlock[] blocks = currentProgram.getMemory().getBlocks();
        for (MemoryBlock b : blocks) {
            w.println("  " + b.getName() + ": " + b.getStart() + ".." +
                      b.getEnd() + " exec=" + b.isExecute());
        }

        Listing listing = currentProgram.getListing();
        ReferenceManager refMan = currentProgram.getReferenceManager();
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Address start = imageBase.add(startRva);
        Function startFunc = currentProgram.getFunctionManager()
                .getFunctionContaining(start);
        if (startFunc == null) {
            w.println("ERROR: no function at RVA 0x" + Long.toHexString(startRva));
            decomp.dispose();
            w.close();
            return;
        }

        Set<Long> visited = new HashSet<Long>();
        Deque<Node> queue = new ArrayDeque<Node>();
        queue.add(new Node(startRva, startFunc, 0));

        int total = 0;
        while (!queue.isEmpty() && total < 24) {
            Node node = queue.poll();
            long entryOff = node.func.getEntryPoint().getOffset();
            if (!visited.add(entryOff)) {
                continue;
            }
            total++;
            dumpFunction(w, listing, refMan, decomp, node.func, node.rva, node.depth);

            if (node.depth >= maxDepth) {
                continue;
            }
            int queued = 0;
            ReferenceIterator refs = refMan.getReferencesTo(node.func.getEntryPoint());
            while (refs.hasNext() && queued < maxCallers) {
                Reference ref = refs.next();
                if (!ref.getReferenceType().isCall()) {
                    continue;
                }
                Address from = ref.getFromAddress();
                Function cf = currentProgram.getFunctionManager()
                        .getFunctionContaining(from);
                if (cf == null) {
                    w.println("  (caller @" + from + " has no enclosing function)");
                    continue;
                }
                long cfEntryOff = cf.getEntryPoint().getOffset();
                if (visited.contains(cfEntryOff)) {
                    continue;
                }
                long rva = cfEntryOff - imageBase.getOffset();
                w.println("### queued caller: " + cf.getName() + " RVA 0x" +
                          Long.toHexString(rva) + " (call @" + from + ")");
                queue.add(new Node(rva, cf, node.depth + 1));
                queued++;
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath + " functions=" + total);
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

    private boolean isExecutable(Address a) {
        MemoryBlock b = currentProgram.getMemory().getBlock(a);
        return b != null && b.isExecute();
    }

    private void dumpFunction(PrintWriter w, Listing listing,
                              ReferenceManager refMan, DecompInterface decomp,
                              Function func, long rva, int depth) throws Exception {
        w.println("");
        w.println("### L" + depth + " RVA 0x" + Long.toHexString(rva) +
                  " -> " + func.getEntryPoint() + "  [FUNC " + func.getName() + "]");
        long bodySize = func.getBody().getMaxAddress().getOffset() -
                        func.getBody().getMinAddress().getOffset();
        w.println("entry: " + func.getEntryPoint() + "  body: " +
                  func.getBody().getMinAddress() + ".." +
                  func.getBody().getMaxAddress() +
                  "  size=" + Long.toHexString(bodySize));

        w.println("--- disassembly (first 80 instrs; <<< GLOBAL = data ref) ---");
        Instruction fi = listing.getInstructionAt(func.getEntryPoint());
        int n = 0;
        while (fi != null && n < 80) {
            StringBuilder sb = new StringBuilder("  " + fi.getAddress() + ": " + fi.toString());
            Reference[] refs = fi.getReferencesFrom();
            if (refs != null) {
                for (Reference r : refs) {
                    Address to = r.getToAddress();
                    if (to != null && !isExecutable(to)) {
                        sb.append("   <<< GLOBAL " + to);
                    }
                }
            }
            w.println(sb.toString());
            fi = fi.getNext();
            n++;
        }
        if (fi != null) {
            w.println("  ... (more)");
        }

        // full-body scan for references into non-executable memory
        w.println("--- global data refs (body scan, deduped) ---");
        TreeSet<String> globals = new TreeSet<String>();
        fi = listing.getInstructionAt(func.getEntryPoint());
        int scanned = 0;
        while (fi != null && scanned < 4000) {
            Reference[] refs = fi.getReferencesFrom();
            if (refs != null) {
                for (Reference r : refs) {
                    Address to = r.getToAddress();
                    if (to != null && !isExecutable(to)) {
                        globals.add("  " + to + "  " + r.getReferenceType() +
                                    "  @" + fi.getAddress());
                    }
                }
            }
            fi = fi.getNext();
            scanned++;
        }
        if (globals.isEmpty()) {
            w.println("  (none)");
        } else {
            for (String g : globals) {
                w.println(g);
            }
        }

        DecompileResults results = decomp.decompileFunction(func, 60, monitor);
        if (results.decompileCompleted()) {
            w.println("--- decompiled (C) ---");
            w.println(results.getDecompiledFunction().getC());
        } else {
            w.println("(decompile failed: " + results.getErrorMessage() + ")");
        }

        w.println("--- callers ---");
        ReferenceIterator refs = refMan.getReferencesTo(func.getEntryPoint());
        int shown = 0;
        while (refs.hasNext() && shown < 12) {
            Reference ref = refs.next();
            if (ref.getReferenceType().isCall()) {
                w.println("  CALL " + ref.getFromAddress() + " " +
                          ref.getReferenceType());
                shown++;
            }
        }
        if (shown == 0) {
            w.println("  (no call callers)");
        }
    }
}
