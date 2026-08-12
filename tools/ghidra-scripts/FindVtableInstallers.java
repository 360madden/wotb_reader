// FindVtableInstallers.java - find code sites that write an immediate in a
// candidate vtable range into memory ([reg + disp], imm32). Constructors
// install the vtable this way; their callers are the object-family creators.
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript FindVtableInstallers.java 0x3677e8c \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-l3-install.log

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.scalar.Scalar;

public class FindVtableInstallers extends GhidraScript {

    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;

    @Override
    public void run() throws Exception {
        long target = 0;
        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (t.startsWith("0x") || t.startsWith("0X")) {
                target = Long.decode(t);
            }
        }
        if (target == 0) {
            println("ERROR: need vtable base address arg");
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
        PrintWriter pw = new PrintWriter(new File(dir, "vtable-installers.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.find-vtable-installers.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        pw.println();
        pw.println("## immediate writes of vtable base 0x" +
                   Long.toHexString(target));
        pw.println();

        Listing listing = currentProgram.getListing();
        FunctionManager fm = currentProgram.getFunctionManager();
        AddressSet range = new AddressSet(toAddr(CODE_LO + 0x1000),
                                          toAddr(CODE_HI));
        int count = 0;
        for (Instruction insn : listing.getInstructions(range, true)) {
            String mnem = insn.getMnemonicString();
            if (!"MOV".equals(mnem)) {
                continue;
            }
            int nOps = insn.getNumOperands();
            if (nOps < 2) {
                continue;
            }
            Object[] refs = insn.getOpObjects(1);
            if (refs == null || refs.length == 0) {
                continue;
            }
            Object imm = refs[0];
            long v = -1;
            if (imm instanceof Scalar) {
                v = ((Scalar) imm).getUnsignedValue();
            } else {
                continue;
            }
            if (v != target) {
                continue;
            }
            Address from = insn.getAddress();
            Function f = fm.getFunctionContaining(from);
            String fn = (f == null) ? "?" : f.getName();
            pw.println("site=0x" + Long.toHexString(from.getOffset()) +
                       " fn=" + fn + " text=" + insn.toString());
            count++;
        }
        pw.println();
        pw.println("count=" + count);
        pw.close();
        println("WROTE " + new File(dir, "vtable-installers.txt").getAbsolutePath());
    }
}
