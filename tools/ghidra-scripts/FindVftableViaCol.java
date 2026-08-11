// FindVftableViaCol.java - locate the TRUE vftable for a class by scanning
// .rdata for pointers back to its RTTI Complete Object Locator. In MSVC,
// every vftable V for class C has *(V-4) == &C::RTTI_Complete_Object_Locator.
// The RTTI symbol lookup can hit the wrong symbol (lambda/function-wrapper
// vtables), so this is the reliable way.
//
// Usage: -postScript FindVftableViaCol.java <COL_RVA> [maxHits]
//
// Hash-bound static evidence only; no live read, no promotion.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;

public class FindVftableViaCol extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long colRva = 0x35cec7cL;
        int maxHits = 12;
        if (args.length > 0 && args[0].startsWith("0x")) {
            colRva = Long.decode(args[0]);
        }
        if (args.length > 1) {
            maxHits = Integer.parseInt(args[1]);
        }
        Address imageBase = currentProgram.getImageBase();
        long colAbs = imageBase.getOffset() + colRva;

        StringBuilder out = new StringBuilder();
        out.append("## vftables referencing COL 0x")
                .append(Long.toHexString(colRva))
                .append(" (absolute 0x").append(Long.toHexString(colAbs))
                .append(")\n\n");

        int hits = 0;
        Memory memory = currentProgram.getMemory();
        Listing listing = currentProgram.getListing();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) {
                continue; // vftables live in .rdata, not .text
            }
            // vftables are in read-only data; skip executable
            continue;
        }
        for (MemoryBlock block : memory.getBlocks()) {
            if (block.isExecute() || !block.isRead()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read;
            try {
                read = memory.getBytes(block.getStart(), data);
            } catch (Exception e) {
                continue;
            }
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 3 < read; i++) {
                long v = (data[i] & 0xffL) | ((data[i + 1] & 0xffL) << 8)
                        | ((data[i + 2] & 0xffL) << 16)
                        | ((data[i + 3] & 0xffL) << 24);
                if (v != colAbs) {
                    continue;
                }
                long vftableRva = block.getStart().add(i + 4).getOffset()
                        - imageBase.getOffset();
                out.append("VFTABLE rva=0x").append(Long.toHexString(vftableRva))
                        .append(" colptr_at=0x").append(Long.toHexString(
                                block.getStart().add(i).getOffset()
                                        - imageBase.getOffset()))
                        .append("\n");
                // dump the vtable slots
                for (int slot = 0; slot < 16; slot++) {
                    long slotAbs = imageBase.getOffset() + vftableRva
                            + (long) slot * 4L;
                    long target = readU32(slotAbs);
                    long trva = target - imageBase.getOffset();
                    if (trva <= 0 || trva > 0x6000000L) {
                        out.append("  slot=").append(slot).append(" <end>\n");
                        break;
                    }
                    Function fn = currentProgram.getFunctionManager()
                            .getFunctionAt(imageBase.add(target));
                    Symbol sym = currentProgram.getSymbolTable()
                            .getPrimarySymbol(imageBase.add(target));
                    out.append("  slot=").append(slot)
                            .append(" target_rva=0x")
                            .append(Long.toHexString(trva))
                            .append(" fn=").append(fn == null ? "<none>"
                                    : fn.getName())
                            .append(" sym=").append(sym == null ? "<none>"
                                    : sym.getName(true))
                            .append("\n");
                }
                out.append("\n");
                hits++;
                if (hits >= maxHits) {
                    break;
                }
                i += 4;
            }
            if (hits >= maxHits) {
                break;
            }
        }
        out.append("total_vftables=").append(hits).append("\n");

        writeReport(out.toString(), hits);
        println("VERDICT vftables=" + hits);
    }

    private long readU32(long absAddress) {
        try {
            byte[] b = new byte[4];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getAddressFactory()
                            .getDefaultAddressSpace().getAddress(absAddress), b);
            if (read != 4) {
                return -1L;
            }
            return (b[0] & 0xffL) | ((b[1] & 0xffL) << 8)
                    | ((b[2] & 0xffL) << 16) | ((b[3] & 0xffL) << 24);
        } catch (Exception e) {
            return -1L;
        }
    }

    private void writeReport(String body, int hits) throws Exception {
        String outPath = getEvidenceOutputPath("find-vftable-via-col.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.find-vftable-via-col.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("vftables=" + hits);
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
