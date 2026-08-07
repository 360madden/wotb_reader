// DumpChain.java - dump the full FUN_00bc3940 + its caller + position-block
// helpers for the FRESH43 write-site root analysis.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpChain.java 0x7C3940 0x7B9B75 0x91A0F0 0x916380 0x9155C0 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\dump-chain.log
//
// RVAs: 0x7C3940 = write-site function (FULL disassembly, up to 400 instrs);
// 0x7B9B75 = caller address (window + enclosing function);
// 0x91A0F0 = FUN_00d1a0f0 (transform extractor);
// 0x916380 = FUN_00d16380 (second block source);
// 0x9155C0 = FUN_00d155c0 (block normalizer).

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class DumpChain extends GhidraScript {

    @Override
    public void run() throws Exception {
        List<Long> rvas = new ArrayList<Long>();
        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (t.startsWith("0x") || t.startsWith("0X")) {
                rvas.add(Long.decode(t));
            }
        }
        if (rvas.isEmpty()) {
            println("ERROR: no RVA argument provided");
            return;
        }
        println("RVAs to dump: " + rvas);

        String outPath = "C:\\work\\wotb_reader\\.freebuff\\worktrees\\ef8b8a29-4baa-44a7-a26a-653c865e8a48\\tools\\ghidra-scripts\\chain-disasm.txt";
        PrintWriter w = new PrintWriter(new File(outPath));

        Listing listing = currentProgram.getListing();
        Address imageBase = currentProgram.getImageBase();
        w.println("=== program: " + currentProgram.getName() +
                  " image_base=" + imageBase + " ===");

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        for (Long rva : rvas) {
            Address target = imageBase.add(rva);
            Function func = currentProgram.getFunctionManager()
                    .getFunctionContaining(target);
            w.println("");
            w.println("### RVA 0x" + Long.toHexString(rva) + " -> address " +
                      target + (func != null ? "  [FUNC " + func.getName() + "]" : ""));

            // For the caller address, dump the enclosing function with a
            // window marker instead of a full dump, unless it's small.
            boolean isCallerAddr = (rva == 0x7B9B75L);
            if (func == null) {
                w.println("  (no enclosing function)");
                continue;
            }
            long bodySize = func.getBody().getMaxAddress().getOffset() -
                            func.getBody().getMinAddress().getOffset();
            int maxInstr = isCallerAddr ? 60 : 400;
            if (isCallerAddr) {
                // dump window around the call first
                w.println("--- call-site window (-24 .. +32 bytes) ---");
                Address ws = target.subtract(24);
                Address we = target.add(32);
                Instruction wi = listing.getInstructionAt(ws);
                if (wi == null) {
                    wi = listing.getInstructionContaining(ws);
                }
                while (wi != null && wi.getAddress().compareTo(we) <= 0) {
                    String m = wi.getAddress().equals(target) ? "   <<< CALL" : "";
                    w.println("  " + wi.getAddress() + ": " + wi.toString() + m);
                    wi = wi.getNext();
                }
            }

            w.println("entry: " + func.getEntryPoint() + "  body: " +
                      func.getBody().getMinAddress() + ".." +
                      func.getBody().getMaxAddress() +
                      "  size=" + Long.toHexString(bodySize));

            w.println("--- disassembly (first " + maxInstr + " instrs) ---");
            Instruction fi = listing.getInstructionAt(func.getEntryPoint());
            int n = 0;
            while (fi != null && n < maxInstr) {
                w.println("  " + fi.getAddress() + ": " + fi.toString());
                fi = fi.getNext();
                n++;
            }
            if (fi != null) {
                w.println("  ... (" + bodySize / 5 + " total bytes)");
            }

            DecompileResults results = decomp.decompileFunction(func, 60, monitor);
            if (results.decompileCompleted()) {
                w.println("--- decompiled (C) ---");
                w.println(results.getDecompiledFunction().getC());
            } else {
                w.println("(decompile failed: " + results.getErrorMessage() + ")");
            }

            if (!isCallerAddr) {
                w.println("--- callers ---");
                ReferenceIterator refs = currentProgram.getReferenceManager()
                        .getReferencesTo(func.getEntryPoint());
                int shown = 0;
                while (refs.hasNext() && shown < 12) {
                    Reference ref = refs.next();
                    w.println("  " + ref.getFromAddress() + " " +
                              ref.getReferenceType());
                    shown++;
                }
                if (shown == 0) {
                    w.println("  (no callers)");
                }
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath);
    }
}
