// DumpRange.java - dump a raw disassembly window for an RVA range, so call-site
// `this`/argument setup is visible around a specific helper call.

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;

public class DumpRange extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long start = 0x1ab6ba0L;
        long end = 0x1ab9c60L;
        if (args.length > 0 && args[0].startsWith("0x")) {
            start = Long.decode(args[0]);
        }
        if (args.length > 1 && args[1].startsWith("0x")) {
            end = Long.decode(args[1]);
        }
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null || outDir.trim().isEmpty()) {
            outDir = "C:\\work\\wotb_reader\\.build\\ghidra-evidence-weapon-install";
        }
        File dir = new File(outDir);
        if (!dir.exists() && !dir.mkdirs()) {
            throw new IllegalStateException("Could not create " + dir);
        }
        PrintWriter pw = new PrintWriter(new File(dir, "range-disasm.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.dump-range.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        pw.println("range=0x" + Long.toHexString(start) + "..0x" + Long.toHexString(end));
        pw.println();

        Listing listing = currentProgram.getListing();
        long imageBase = currentProgram.getImageBase().getOffset();
        Address addr = currentProgram.getAddressFactory().getDefaultAddressSpace()
                .getAddress(imageBase + start);
        Address endAddr = currentProgram.getAddressFactory().getDefaultAddressSpace()
                .getAddress(imageBase + end);
        Instruction insn = listing.getInstructionAt(addr);
        if (insn == null) {
            insn = listing.getInstructionContaining(addr);
        }
        int n = 0;
        while (insn != null && insn.getAddress().compareTo(endAddr) <= 0 && n < 600) {
            pw.println("  " + insn.getAddress() + ": " + insn.toString());
            insn = insn.getNext();
            n++;
        }
        pw.close();
        println("WROTE " + new File(dir, "range-disasm.txt").getAbsolutePath()
                + " instructions=" + n);
    }
}
