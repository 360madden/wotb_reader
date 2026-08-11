// ScanAllClockOffsetStores.java - every instruction in the executable that
// references [reg+0x90], grouped by enclosing function, with store/load
// classification. The replay clock is a Double at [subobj+0x90]; its write
// site is one of the store-classified hits (FSTP / MOVSD / FADD-family /
// ADDSD / integer MOV, etc.).

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.TreeMap;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ScanAllClockOffsetStores extends GhidraScript {

    private final TreeMap<Long, List<String>> storeByFunc =
            new TreeMap<Long, List<String>>();
    private final TreeMap<Long, List<String>> loadByFunc =
            new TreeMap<Long, List<String>>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();

        out.append("## scan: all instructions referencing [reg+0x90]\n\n");

        int storeCount = 0;
        int loadCount = 0;
        AddressSet execSet = new AddressSet();
        for (MemoryBlock block : memory.getBlocks()) {
            if (block.isExecute()) {
                execSet.add(block.getStart(), block.getEnd());
            }
        }
        InstructionIterator all = listing.getInstructions(execSet, true);
        while (all.hasNext() && monitor.isCancelled() == false) {
            Instruction instr = all.next();
            String text = instr.toString();
            if (!text.contains("0x90]")) {
                continue;
            }
            long siteRva = instr.getAddress().getOffset() - imageBase.getOffset();
            Function fn = currentProgram.getFunctionManager()
                    .getFunctionContaining(instr.getAddress());
            String line = "site=0x" + Long.toHexString(siteRva)
                    + (fn == null ? " fn=<none>" :
                            " fn=0x" + Long.toHexString(
                                    fn.getEntryPoint().getOffset()
                                            - imageBase.getOffset())
                            + " " + fn.getName())
                    + " " + text;
            String mnem = instr.getMnemonicString();
            boolean isStore = mnem.equals("FSTP") || mnem.equals("MOVSD")
                    || mnem.equals("MOV") || mnem.equals("FADD")
                    || mnem.equals("FADDP") || mnem.equals("ADDSD")
                    || mnem.equals("ADD") || mnem.equals("FST")
                    || mnem.equals("MOVLPD") || mnem.equals("MOVHPD")
                    || mnem.equals("FILD") || mnem.equals("FISTP")
                    || mnem.equals("FCMOV") || mnem.equals("FUCOM")
                    || mnem.equals("FCOMI") || mnem.equals("FCOMIP")
                    || mnem.equals("FCOM") || mnem.equals("FCOMP");
            // memory operand position: destination vs source
            // (heuristic: MOVSD XMM,[mem] is a load; MOVSD [mem],XMM is a store)
            boolean isStoreByOrder = isStore;
            if (mnem.equals("MOVSD") || mnem.equals("MOV")) {
                int memIdx = text.indexOf("0x90]");
                isStoreByOrder = memIdx > 0 && memIdx < 20;
            }
            long fnKey = fn == null ? -1L
                    : fn.getEntryPoint().getOffset() - imageBase.getOffset();
            if (isStoreByOrder) {
                storeCount++;
                storeByFunc.computeIfAbsent(fnKey, k -> new ArrayList<String>())
                        .add(line);
            } else {
                loadCount++;
                loadByFunc.computeIfAbsent(fnKey, k -> new ArrayList<String>())
                        .add(line);
            }
        }

        out.append("total_stores=").append(storeCount)
                .append(" total_loads=").append(loadCount).append("\n");
        out.append("store_functions=").append(storeByFunc.size())
                .append(" load_functions=").append(loadByFunc.size()).append("\n\n");

        out.append("## stores by function\n");
        for (java.util.Map.Entry<Long, List<String>> e : storeByFunc.entrySet()) {
            out.append(e.getKey() < 0 ? "fn=<none>" : "fn=0x"
                    + Long.toHexString(e.getKey())).append(" count=")
                    .append(e.getValue().size()).append("\n");
            for (String line : e.getValue()) {
                out.append("  ").append(line).append("\n");
            }
        }
        out.append("\n## loads by function\n");
        for (java.util.Map.Entry<Long, List<String>> e : loadByFunc.entrySet()) {
            out.append(e.getKey() < 0 ? "fn=<none>" : "fn=0x"
                    + Long.toHexString(e.getKey())).append(" count=")
                    .append(e.getValue().size()).append("\n");
            for (String line : e.getValue()) {
                out.append("  ").append(line).append("\n");
            }
        }

        writeReport(out.toString(), storeCount, loadCount);
        println("VERDICT stores=" + storeCount + " loads=" + loadCount);
    }

    private void writeReport(String body, int stores, int loads)
            throws Exception {
        String outPath = getEvidenceOutputPath("scan-all-clock-offset-stores.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.scan-all-clock-offset-stores.v2");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("scan=all_instructions_referencing_plus_0x90");
        w.println("stores=" + stores);
        w.println("loads=" + loads);
        w.println();
        w.print(body);
        w.close();
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
