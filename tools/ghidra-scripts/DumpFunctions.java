// DumpFunctions.java - full-function dump for the FRESH43 write-site chain.
//
// Usage (project already analyzed; use -noanalysis to skip re-analysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpFunctions.java 0x7C3940 0x929EA0 0x329570 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\tools\ghidra-scripts\dump-functions.log
//
// Each arg is an RVA; the script dumps the enclosing function's full
// disassembly (first 120 instrs) + decompile + callers.

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

public class DumpFunctions extends GhidraScript {

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

        String outPath = getEvidenceOutputPath("functions-disasm.txt");
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
            if (func == null) {
                w.println("  (no enclosing function)");
                continue;
            }
            w.println("entry: " + func.getEntryPoint() + "  body: " +
                      func.getBody().getMinAddress() + ".." +
                      func.getBody().getMaxAddress());

            // full function disassembly (first 120)
            w.println("--- disassembly (first 120 instrs) ---");
            Instruction fi = listing.getInstructionAt(func.getEntryPoint());
            int n = 0;
            while (fi != null && n < 120) {
                String marker = "";
                Address end = fi.getAddress().add(fi.getLength() - 1);
                if (target.compareTo(fi.getAddress()) >= 0 &&
                    target.compareTo(end) <= 0) {
                    marker = "   <<< TARGET";
                }
                w.println("  " + fi.getAddress() + ": " + fi.toString() + marker);
                fi = fi.getNext();
                n++;
            }

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
            } else if (refs.hasNext()) {
                w.println("  ... (more callers)");
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath);
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
