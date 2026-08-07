// DumpWriteSite.java - Ghidra headless script for the FRESH43 write-site
// analysis. Disassembles + decompiles the function enclosing wotblitz.exe
// RVA 0x7C39AB (the per-frame fill site caught live by the FRESH43
// source-arm) and dumps the disassembly window + callers.
//
// Usage (analyzed project WotBlitz must already exist):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe \
//       -postScript DumpWriteSite.java 0x7C39AB \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\dump-writesite.log
//
// Optional extra RVAs: 0x7C39AB 0xE8AE 0xED49

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

public class DumpWriteSite extends GhidraScript {

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

        String outPath = "C:\\work\\wotb_reader\\.freebuff\\worktrees\\ef8b8a29-4baa-44a7-a26a-653c865e8a48\\tools\\ghidra-scripts\\writesite-disasm.txt";
        PrintWriter w = new PrintWriter(new File(outPath));

        Listing listing = currentProgram.getListing();
        Address imageBase = currentProgram.getImageBase();
        w.println("=== program: " + currentProgram.getName() +
                  " image_base=" + imageBase + " ===");

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        for (Long rva : rvas) {
            Address target = imageBase.add(rva);
            w.println("");
            w.println("### RVA 0x" + Long.toHexString(rva) + " -> address " + target);

            // disassembly window: -16 .. +48 bytes around target
            w.println("--- disassembly window ---");
            Address start = target.subtract(16);
            Address end = target.add(48);
            Instruction instr = listing.getInstructionAt(start);
            if (instr == null) {
                instr = listing.getInstructionContaining(start);
            }
            while (instr != null && instr.getAddress().compareTo(end) <= 0) {
                String marker = instr.getAddress().equals(target) ? "  <<< RIP" : "";
                w.println(instr.getAddress() + ": " + instr.toString() + marker);
                instr = instr.getNext();
            }

            // enclosing function
            Function func = currentProgram.getFunctionManager()
                    .getFunctionContaining(target);
            if (func == null) {
                w.println("--- enclosing function: NOT FOUND ---");
            } else {
                w.println("--- enclosing function ---");
                w.println("name: " + func.getName());
                w.println("entry: " + func.getEntryPoint() + "  body: " +
                          func.getBody().getMinAddress() + ".." +
                          func.getBody().getMaxAddress());

                // decompile
                DecompileResults results = decomp.decompileFunction(func, 60, monitor);
                if (results.decompileCompleted()) {
                    w.println("--- decompiled (C) ---");
                    w.println(results.getDecompiledFunction().getC());
                } else {
                    w.println("(decompile failed: " + results.getErrorMessage() + ")");
                }

                // callers
                w.println("--- callers ---");
                ghidra.program.model.symbol.ReferenceIterator refs =
                        currentProgram.getReferenceManager()
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
                } else if (refs.hasNext()) {
                    w.println("  ... (more callers)");
                }

                // function head
                w.println("--- function head (first 40 instrs) ---");
                Instruction fi = listing.getInstructionAt(func.getEntryPoint());
                int n = 0;
                while (fi != null && n < 40) {
                    w.println("  " + fi.getAddress() + ": " + fi.toString());
                    fi = fi.getNext();
                    n++;
                }
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath);
    }
}
