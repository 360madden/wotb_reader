// DumpRawBytes.java - dump raw bytes at one or more RVAs and force
// disassembly from those addresses (useful for thunk/region gaps where
// Ghidra's listing has undefined bytes).
//
// Usage: analyzeHeadless ... -postScript DumpRawBytes.java 0x22f9130 0x2707b80
//   Each arg: <RVA>[:count] e.g. 0x2707b80:64

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;

public class DumpRawBytes extends GhidraScript {

    @Override
    public void run() throws Exception {
        List<long[]> ranges = new ArrayList<long[]>();
        for (String a : getScriptArgs()) {
            String t = a.trim();
            long count = 32;
            String rvaPart = t;
            int colon = t.indexOf(':');
            if (colon >= 0) {
                rvaPart = t.substring(0, colon);
                count = Long.decode(t.substring(colon + 1));
            }
            if (rvaPart.startsWith("0x") || rvaPart.startsWith("0X")) {
                ranges.add(new long[] { Long.decode(rvaPart), count });
            }
        }
        if (ranges.isEmpty()) {
            println("ERROR: no RVA argument provided");
            return;
        }

        String outPath = getEvidenceOutputPath("raw-bytes.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        Address imageBase = currentProgram.getImageBase();
        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();

        for (long[] range : ranges) {
            long rva = range[0];
            long count = range[1];
            Address start = imageBase.add(rva);
            w.println("### RVA 0x" + Long.toHexString(rva) + " -> " + start +
                    " count=" + count);
            byte[] bytes = new byte[(int) count];
            int read = memory.getBytes(start, bytes);
            StringBuilder hex = new StringBuilder();
            StringBuilder ascii = new StringBuilder();
            for (int i = 0; i < read; i++) {
                int value = bytes[i] & 0xff;
                hex.append(String.format(Locale.ROOT, "%02x ", value));
                ascii.append(value >= 32 && value < 127 ? (char) value : '.');
                if ((i + 1) % 16 == 0 || i == read - 1) {
                    w.println("  " + start.add(i / 16 * 16) + ": " + hex +
                            "  " + ascii);
                    hex.setLength(0);
                    ascii.setLength(0);
                }
            }
            w.println("  bytes_read=" + read);
            Instruction instr = listing.getInstructionAt(start);
            if (instr == null) {
                instr = listing.getInstructionContaining(start);
            }
            if (instr != null) {
                w.println("  enclosing_instruction=" + instr.getAddress() +
                        ": " + instr);
            }
            Function atFn = currentProgram.getFunctionManager()
                    .getFunctionAt(start);
            Function containingFn = currentProgram.getFunctionManager()
                    .getFunctionContaining(start);
            w.println("  function_at=" + (atFn == null ? "<none>" : atFn.getName()));
            w.println("  function_containing=" +
                    (containingFn == null ? "<none>" : containingFn.getName()));
            if (atFn != null) {
                w.println("  function_at_body=" + atFn.getBody().getMinAddress() +
                        ".." + atFn.getBody().getMaxAddress());
            }
            if (instr == null) {
                w.println("  <no decoded instruction at target; forcing>");
                try {
                    boolean ok = disassemble(start);
                    w.println("  forced_ok=" + ok);
                    Instruction forced = listing.getInstructionAt(start);
                    if (forced != null) {
                        w.println("  forced_first=" + forced);
                    }
                } catch (Exception e) {
                    w.println("  forced_failed: " + e.getMessage());
                }
            }
        }
        w.close();
        println("WROTE " + outPath);
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build\\ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs()) {
            throw new IllegalStateException(
                    "Could not create Ghidra evidence directory");
        }
        return new File(directory, fileName).getAbsolutePath();
    }
}
