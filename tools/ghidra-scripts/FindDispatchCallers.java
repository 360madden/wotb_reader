// FindDispatchCallers.java - find CALL dword ptr [reg + disp] dispatch sites
// for a vtable slot offset (virtual call through the vehicle vtable), and
// dump the enclosing function + a window around each site.
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript FindDispatchCallers.java 0x30 0x34 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-l3-dispatch.log
//
// Args: one or more slot dispatch offsets (hex).

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;

public class FindDispatchCallers extends GhidraScript {

    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;

    @Override
    public void run() throws Exception {
        java.util.List<Long> offsets = new java.util.ArrayList<Long>();
        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (t.startsWith("0x") || t.startsWith("0X")) {
                offsets.add(Long.decode(t));
            }
        }
        if (offsets.isEmpty()) {
            println("ERROR: need dispatch offsets (hex)");
            return;
        }

        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null) {
            outDir = "C:\\work\\wotb_reader\\.build";
        }
        File dir = new File(outDir);
        if (!dir.exists()) {
            dir.mkdirs();
        }
        PrintWriter pw = new PrintWriter(new File(dir, "dispatch-callers.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.find-dispatch-callers.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        pw.println();

        Listing listing = currentProgram.getListing();
        FunctionManager fm = currentProgram.getFunctionManager();
        AddressSet range = new AddressSet(toAddr(CODE_LO + 0x1000),
                                          toAddr(CODE_HI));
        for (Long off : offsets) {
            pw.println("## CALL [reg + 0x" + Long.toHexString(off) + "] sites");
            int count = 0;
            for (Instruction insn : listing.getInstructions(range, true)) {
                String mnem = insn.getMnemonicString();
                if (!"CALL".equals(mnem)) {
                    continue;
                }
                String text = insn.toString();
                if (!text.matches(".*\\[.*\\+ 0x" + Long.toHexString(off) + "\\].*")) {
                    continue;
                }
                Address from = insn.getAddress();
                Function f = fm.getFunctionContaining(from);
                String fn = (f == null) ? "?" : f.getName();
                pw.println("site=0x" + Long.toHexString(from.getOffset()) +
                           " fn=" + fn + " text=" + text);
                count++;
            }
            pw.println("count=" + count);
            pw.println();
        }
        pw.close();
        println("WROTE " + new File(dir, "dispatch-callers.txt").getAbsolutePath());
    }
}
