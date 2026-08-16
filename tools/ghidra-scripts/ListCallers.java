// ListCallers.java - compact direct-caller listing for one or more function
// RVAs. Used to trace who constructs the weapon-owning objects.
//
//   -postScript ListCallers.java 0x1683b00 0x16d1b60 0x1636520 ...

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class ListCallers extends GhidraScript {

    @Override
    public void run() throws Exception {
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null || outDir.trim().isEmpty()) {
            outDir = "C:\\work\\wotb_reader\\.build\\ghidra-evidence-weapon-install";
        }
        File dir = new File(outDir);
        if (!dir.exists() && !dir.mkdirs()) {
            throw new IllegalStateException("Could not create " + dir);
        }
        PrintWriter pw = new PrintWriter(new File(dir, "callers.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.list-callers.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        pw.println();

        FunctionManager fm = currentProgram.getFunctionManager();
        long imageBase = currentProgram.getImageBase().getOffset();
        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (!(t.startsWith("0x") || t.startsWith("0X"))) {
                continue;
            }
            // Args are ABSOLUTE addresses (matching the FUN_0x... names);
            // also tolerate an RVA by checking imageBase+arg when direct fails.
            long abs = Long.decode(t);
            Address entry = currentProgram.getAddressFactory()
                    .getDefaultAddressSpace().getAddress(abs);
            Function f = fm.getFunctionAt(entry);
            if (f == null) {
                Address viaBase = currentProgram.getAddressFactory()
                        .getDefaultAddressSpace().getAddress(imageBase + abs);
                f = fm.getFunctionAt(viaBase);
                if (f != null) {
                    entry = viaBase;
                }
            }
            pw.println("## function " + (f == null
                    ? "0x" + Long.toHexString(abs)
                    : f.getName() + " (rva 0x" + Long.toHexString(
                            entry.getOffset() - imageBase) + ")"));
            if (f == null) {
                pw.println();
                continue;
            }
            ReferenceIterator refs = currentProgram.getReferenceManager()
                    .getReferencesTo(entry);
            int n = 0;
            while (refs.hasNext()) {
                Reference r = refs.next();
                if (!r.getReferenceType().isCall()) {
                    continue;
                }
                Address from = r.getFromAddress();
                Function cf = fm.getFunctionContaining(from);
                pw.println("  CALL @0x" + Long.toHexString(from.getOffset() - imageBase)
                        + " fn=" + (cf == null ? "<none>" : cf.getName()));
                n++;
            }
            if (n == 0) {
                pw.println("  (no call callers)");
            }
            pw.println();
        }
        pw.close();
        println("WROTE " + new File(dir, "callers.txt").getAbsolutePath());
    }
}
