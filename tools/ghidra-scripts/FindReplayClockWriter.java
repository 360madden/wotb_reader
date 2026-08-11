// FindReplayClockWriter.java - locate code that reads/writes the replay
// clock Double at subobject+0x90 (the field exposed by the connection's
// slot-10 getter: [this+0x58] + 0x90). Hash-bound; static evidence only.
//
// Iterates decoded .text instructions and keeps any whose operand text
// contains a positive "+ 0x90]" displacement with an 8-byte access
// (qword/double). Negative stack displacements print as "+ -0x70]" and are
// naturally excluded. Reports every site with its enclosing function.

import java.io.File;
import java.io.PrintWriter;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;

public class FindReplayClockWriter extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    @Override
    public void run() throws Exception {
        String hash = currentProgram.getExecutableSHA256();
        PrintWriter w = new PrintWriter(new File(getEvidenceOutputPath(
                "replay-clock-writer-scan.txt")));
        w.println("schema=wotbtreader.ghidra.replay-clock-writer.v2");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + hash);
        w.println("hash_match=" + EXPECTED_SHA256.equalsIgnoreCase(hash));

        MemoryBlock text = null;
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (block.isExecute()) {
                text = block;
                break;
            }
        }
        if (text == null) {
            w.println("ERROR: no executable block found");
            w.close();
            println("VERDICT clock-writer-scan-complete");
            return;
        }
        w.println("text_block=" + text.getName() + " " + text.getStart() +
                ".." + text.getEnd());

        Address imageBase = currentProgram.getImageBase();
        Listing listing = currentProgram.getListing();
        InstructionIterator iter = listing.getInstructions(text.getStart(),
                true);
        int qwordStoreCount = 0;
        int qwordLoadCount = 0;
        int otherCount = 0;
        w.println();
        w.println("## sites (positive [reg+0x90], 8-byte accesses)");
        while (iter.hasNext() && monitor.isCancelled() == false) {
            Instruction instr = iter.next();
            String textStr = instr.toString().toUpperCase(Locale.ROOT);
            if (!textStr.contains("0X90]") &&
                    !textStr.contains("0X90,")) {
                continue;
            }
            boolean qword = textStr.contains("QWORD PTR") ||
                    textStr.contains("DOUBLE PTR");
            if (!qword) {
                otherCount++;
                continue;
            }
            boolean store = textStr.contains("MOVSD QWORD PTR") ||
                    textStr.contains("FSTP") || textStr.contains("MOVQ QWORD PTR");
            boolean load = textStr.contains("MOVSD XMM") ||
                    textStr.contains("FLD") || textStr.contains("MOVQ XMM");
            if (store) {
                qwordStoreCount++;
            } else if (load) {
                qwordLoadCount++;
            } else {
                otherCount++;
            }
            Function fn = currentProgram.getFunctionManager()
                    .getFunctionContaining(instr.getAddress());
            long rva = instr.getAddress().getOffset() - imageBase.getOffset();
            w.println((store ? "STORE" : (load ? "LOAD" : "OTHER")) +
                    " 0x" + Long.toHexString(rva) +
                    " fn=" + (fn == null ? "<none>" : fn.getName()) +
                    " " + instr.toString());
        }
        w.println();
        w.println("qword_store_count=" + qwordStoreCount);
        w.println("qword_load_count=" + qwordLoadCount);
        w.println("other_count=" + otherCount);
        w.close();
        println("VERDICT clock-writer-scan-complete");
        println("WROTE replay-clock-writer-scan.txt stores=" +
                qwordStoreCount + " loads=" + qwordLoadCount);
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
