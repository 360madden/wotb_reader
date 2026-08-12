// FindVtableDispatch.java - locate the vtable containing a given slot and
// dump the code sites that reference the vtable (virtual dispatch callers).
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript FindVtableDispatch.java 0x3675600 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-l3-vtable.log
//
// Arg: the ADDRESS of a slot inside a vtable (the address Ghidra itself
// reports for the DATA reference, e.g. 03675600). The script scans backward
// for the vtable base (a maximal run of pointers into the code space),
// reports the slot index, then lists every code reference to the vtable
// base with the enclosing function entry.

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class FindVtableDispatch extends GhidraScript {

    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;

    @Override
    public void run() throws Exception {
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null) {
            outDir = "C:\\work\\wotb_reader\\.build";
        }
        File dir = new File(outDir);
        if (!dir.exists()) {
            dir.mkdirs();
        }
        PrintWriter pw = new PrintWriter(new File(dir, "vtable-dispatch.txt"), "UTF-8");

        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (!(t.startsWith("0x") || t.startsWith("0X"))) {
                continue;
            }
            long slotAddr = Long.decode(t);
            pw.println("=== slot address 0x" + Long.toHexString(slotAddr) + " ===");
            Memory mem = currentProgram.getMemory();
            Listing listing = currentProgram.getListing();
            FunctionManager fm = currentProgram.getFunctionManager();

            // Scan backward from the slot for the vtable base: a maximal run
            // of dwords that each point into the code space (or are 0).
            long base = slotAddr;
            boolean scanning = true;
            while (scanning) {
                long cand = base - 4;
                long val = 0;
                try {
                    val = mem.getInt(toAddr(cand)) & 0xFFFFFFFFL;
                } catch (Exception e) {
                    break;
                }
                if (val == 0 || (val >= CODE_LO && val < CODE_HI)) {
                    base = cand;
                } else {
                    scanning = false;
                }
            }
            int index = (int) ((slotAddr - base) / 4);
            pw.println("vtable base address 0x" + Long.toHexString(base) +
                       " (slot index " + index + ", dispatch offset 0x" +
                       Long.toHexString(index * 4L) + ")");

            // List every reference TO the vtable base from code.
            Address baseAddr = toAddr(base);
            ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(baseAddr);
            pw.println("references to vtable base:");
            int n = 0;
            while (refs.hasNext() && n < 60) {
                Reference r = refs.next();
                Address from = r.getFromAddress();
                Function f = fm.getFunctionContaining(from);
                String fn = (f == null) ? "?" : f.getName();
                pw.println("  " + from + " fn=" + fn);
                n++;
            }
            if (n == 0) {
                pw.println("  (none)");
            }

            // Slot neighborhood so the family is identifiable.
            pw.println("vtable slots around index " + index + ":");
            for (int i = Math.max(0, index - 3); i <= index + 8; i++) {
                long addr = base + i * 4L;
                long val = 0;
                try {
                    val = mem.getInt(toAddr(addr)) & 0xFFFFFFFFL;
                } catch (Exception e) {
                    val = 0;
                }
                String txt = (val >= CODE_LO && val < CODE_HI)
                        ? (" -> " + listing.getFunctionAt(toAddr(val)))
                        : "";
                pw.println("  [" + i + "] 0x" + String.format("%08x", val) + txt);
            }
            pw.println();
        }
        pw.close();
        println("WROTE " + new File(dir, "vtable-dispatch.txt").getAbsolutePath());
    }
}
