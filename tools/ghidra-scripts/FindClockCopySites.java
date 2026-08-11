// FindClockCopySites.java - find the copy sites that deliver the replay
// clock to [subobj+0x90]. v2: generalizes the sub-object deref pattern to
// ALL source registers (MOV r32,[src+0x58] = 8B /r mod=01 disp8=0x58,
// modrm 0x40..0x7F), fixing the v1 gap that only matched ECX as source.
//
// The clock is never directly stored (exhaustive negative), so it arrives
// via a CRT copy (memcpy/memmove) or rep-movsd loop. This scan lists, for
// every deref function, the CALL targets that are copy functions, with the
// window context so the reviewer can see how dst is computed.
//
// Hash-bound static evidence only; no live read, no promotion.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.TreeSet;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;

public class FindClockCopySites extends GhidraScript {

    private final TreeSet<Long> copyFuncs = new TreeSet<Long>();
    private final List<String> callSites = new ArrayList<String>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();

        // 1. Identify copy functions (symbols + rep-movs loops).
        out.append("## copy-function candidates\n");
        SymbolIterator syms = currentProgram.getSymbolTable().getAllSymbols(true);
        while (syms.hasNext()) {
            Symbol s = syms.next();
            String n = s.getName(true).toLowerCase();
            if (n.contains("memcpy") || n.contains("memmove")
                    || n.contains("memcmp") || n.contains("memset")) {
                long rva = s.getAddress().getOffset() - imageBase.getOffset();
                copyFuncs.add(rva);
                out.append("symbol_copy 0x").append(Long.toHexString(rva))
                        .append(" ").append(s.getName(true)).append("\n");
            }
        }
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 4 < read; i++) {
                boolean isRep = (data[i] == (byte) 0xf3 && data[i + 1] == (byte) 0xa5)
                        || (data[i] == (byte) 0xf3 && data[i + 1] == (byte) 0xa4)
                        || (data[i] == (byte) 0xf3 && data[i + 1] == 0x48
                                && data[i + 2] == (byte) 0xa5);
                if (!isRep) {
                    continue;
                }
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(block.getStart().add(i));
                if (fn != null) {
                    long rva = fn.getEntryPoint().getOffset()
                            - imageBase.getOffset();
                    if (copyFuncs.add(rva)) {
                        out.append("repcopy 0x").append(Long.toHexString(rva))
                                .append(" ").append(fn.getName())
                                .append(" (rep-movs loop)\n");
                    }
                }
            }
        }
        out.append("copy_function_count=").append(copyFuncs.size()).append("\n\n");

        // 2. General sub-object deref scan: MOV r32,[src+0x58].
        //    Bytes: 8B <modrm 0x40..0x7F with mod=01> 58
        TreeSet<Long> derefFuncs = new TreeSet<Long>();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 2 < read; i++) {
                if (data[i] != (byte) 0x8b) {
                    continue;
                }
                int modrm = data[i + 1] & 0xff;
                int mod = (modrm >> 6) & 0x3;
                if (mod != 1) {           // disp8
                    continue;
                }
                if (data[i + 2] != 0x58) { // disp8 = 0x58
                    continue;
                }
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(block.getStart().add(i));
                if (fn != null) {
                    derefFuncs.add(fn.getEntryPoint().getOffset()
                            - imageBase.getOffset());
                }
            }
        }
        out.append("deref_function_count=").append(derefFuncs.size()).append("\n");

        // 3. Inside each deref function, list CALLs to copy functions.
        out.append("\n## copy CALL sites inside deref functions\n");
        for (Long fnRva : derefFuncs) {
            Function fn = currentProgram.getFunctionManager()
                    .getFunctionAt(imageBase.add(fnRva));
            if (fn == null) {
                continue;
            }
            InstructionIterator iter = listing.getInstructions(fn.getBody(), true);
            while (iter.hasNext() && monitor.isCancelled() == false) {
                Instruction instr = iter.next();
                if (!instr.getMnemonicString().equals("CALL")
                        || instr.getFlows() == null
                        || instr.getFlows().length == 0) {
                    continue;
                }
                long targetRva = instr.getFlows()[0].getOffset()
                        - imageBase.getOffset();
                if (!copyFuncs.contains(targetRva)) {
                    continue;
                }
                long siteRva = instr.getAddress().getOffset()
                        - imageBase.getOffset();
                String line = "CALLSITE fn=0x" + Long.toHexString(fnRva)
                        + " (" + fn.getName() + ") site=0x"
                        + Long.toHexString(siteRva)
                        + " -> copy 0x" + Long.toHexString(targetRva)
                        + " " + instr.toString();
                callSites.add(line);
                out.append(">>> ").append(line).append("\n");
            }
        }
        out.append("\ncall_site_count=").append(callSites.size()).append("\n");

        // 4. Copy call sites NOT inside deref functions but near the
        //    connection region (RVA 0x22f0000..0x2710000) — the connection
        //    and its sub-object live there; a writer may compute the
        //    sub-object pointer without a [reg+0x58] deref (e.g., via
        //    [this+0x4] or a saved pointer).
        out.append("\n## copy CALL sites in the connection/subobj RVA band (0x22f0000-0x2710000)\n");
        int bandCount = 0;
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 5 < read; i++) {
                if (data[i] != (byte) 0xe8) {   // CALL rel32
                    continue;
                }
                long siteRva = block.getStart().add(i).getOffset()
                        - imageBase.getOffset();
                if (siteRva < 0x22f0000L || siteRva > 0x2710000L) {
                    continue;
                }
                int rel = (data[i + 1] & 0xff)
                        | ((data[i + 2] & 0xff) << 8)
                        | ((data[i + 3] & 0xff) << 16)
                        | ((data[i + 4] & 0xff) << 24);
                long targetRva = siteRva + 5 + rel;
                if (!copyFuncs.contains(targetRva)) {
                    continue;
                }
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(block.getStart().add(i));
                String line = "BAND fn=" + (fn == null ? "<none>"
                        : "0x" + Long.toHexString(fn.getEntryPoint().getOffset()
                                - imageBase.getOffset()) + " " + fn.getName())
                        + " site=0x" + Long.toHexString(siteRva)
                        + " -> copy 0x" + Long.toHexString(targetRva);
                out.append(">>> ").append(line).append("\n");
                bandCount++;
                i += 4;
            }
        }
        out.append("\nband_call_site_count=").append(bandCount).append("\n");

        writeReport(out.toString(), copyFuncs.size(), derefFuncs.size(),
                callSites.size(), bandCount);
        println("VERDICT copies=" + copyFuncs.size()
                + " deref_fns=" + derefFuncs.size()
                + " call_sites=" + callSites.size()
                + " band=" + bandCount);
    }

    private void writeReport(String body, int copies, int derefs, int calls,
                             int band) throws Exception {
        String outPath = getEvidenceOutputPath("find-clock-copy-sites.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.find-clock-copy-sites.v2");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("scan=copy_callsites_general_deref_pattern");
        w.println("copy_functions=" + copies);
        w.println("deref_functions=" + derefs);
        w.println("copy_call_sites=" + calls);
        w.println("band_call_sites=" + band);
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
