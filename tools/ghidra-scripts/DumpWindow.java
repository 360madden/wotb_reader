// DumpWindow.java - dump a wide disassembly window around call sites so the
// `this`/ECX load before the call is visible (Ghidra's decompiler loses it).
// Also dumps a full small function if the RVA is a function entry.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpWindow.java 0x12528D4 0x4EE9F0 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\dump-window.log
//
// Each arg is an RVA: prints a window from -96 bytes to +32 bytes around it
// with a <<< TARGET marker, the enclosing function's entry/body, and a
// decompile of the enclosing function.

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

public class DumpWindow extends GhidraScript {

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
        println("RVAs to window: " + rvas);

        String outPath =
            "C:\\work\\wotb_reader\\.freebuff\\worktrees\\" +
            "ef8b8a29-4baa-44a7-a26a-653c865e8a48\\tools\\ghidra-scripts\\" +
            "window-disasm.txt";
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

            w.println("--- window (-96 .. +32 bytes) ---");
            Address start = target.subtract(96);
            Address end = target.add(32);
            Instruction instr = listing.getInstructionAt(start);
            if (instr == null) {
                instr = listing.getInstructionContaining(start);
            }
            while (instr != null && instr.getAddress().compareTo(end) <= 0) {
                String marker = instr.getAddress().equals(target) ? "   <<< TARGET" : "";
                w.println("  " + instr.getAddress() + ": " + instr.toString() + marker);
                instr = instr.getNext();
            }

            if (func == null) {
                w.println("  (no enclosing function)");
                continue;
            }
            w.println("--- enclosing function ---");
            w.println("name: " + func.getName() + "  entry: " + func.getEntryPoint() +
                      "  body: " + func.getBody().getMinAddress() + ".." +
                      func.getBody().getMaxAddress());
            DecompileResults results = decomp.decompileFunction(func, 60, monitor);
            if (results.decompileCompleted()) {
                w.println("--- decompiled (C) ---");
                w.println(results.getDecompiledFunction().getC());
            } else {
                w.println("(decompile failed: " + results.getErrorMessage() + ")");
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath);
    }
}
