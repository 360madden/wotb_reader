// FindVftableForType.java - reverse RTTI lookup: from a mangled type-name
// string address, find the TypeDescriptor, every RTTI Complete Object
// Locator that references it, and every vftable whose (vftable-4) points
// back to one of those COLs. This resolves a class's TRUE vftable(s) from
// its RTTI name (the reliable direction when the RTTI symbol lookup hits a
// lambda/function-wrapper vtable).
//
// MSVC x86 layout: TypeDescriptor { pVFTable+0x00, spare+0x04, name+0x08 }
// where the mangled name string lives AT +0x08. COL { signature+0x00,
// offset+0x04, cdOffset+0x08, pTypeDescriptor+0x0C, pHierarchy+0x10,
// pSelf+0x14 } (x86 32-bit). Every vftable V satisfies *(V-4) == &COL.
//
// Usage: -postScript FindVftableForType.java <nameStringAbs> [maxVftables]
// where nameStringAbs is the ABSOLUTE address of the mangled name string
// (as printed by the RTTI scan, e.g. 0x4278660 for ReplayCameraController).
//
// Hash-bound static evidence only; no live read, no promotion.
import java.io.File;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class FindVftableForType extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long nameRva = 0x4278660L;
        int maxVftables = 20;
        if (args.length > 0 && args[0].startsWith("0x")) {
            nameRva = Long.decode(args[0]);
        }
        if (args.length > 1) {
            maxVftables = Integer.parseInt(args[1]);
        }
        Address imageBase = currentProgram.getImageBase();
        long base = imageBase.getOffset();

        StringBuilder out = new StringBuilder();
        out.append("## Reverse RTTI for name string 0x")
           .append(Long.toHexString(nameRva)).append("\n\n");

        // The RTTI scan prints absolute addresses, so the argument is
        // absolute: do NOT add the image base again.
        long nameAbs = nameRva;
        String name = readCString(nameAbs, 256);
        out.append("name_string=").append(name).append("\n");

        // TypeDescriptor is 8 bytes before the inline name string.
        long typeDescAbs = nameAbs - 8;
        out.append("type_descriptor abs=0x")
           .append(Long.toHexString(typeDescAbs)).append("\n");

        // Pass 1: find COLs referencing the TypeDescriptor (pTypeDescriptor
        // at COL+0x0C). Runs with -noanalysis, so data references are NOT
        // computed: scan readable blocks for every 4-byte pointer equal to
        // the TypeDescriptor. The pointer sits at COL+0x0C; a valid x86 COL
        // has signature 0 at +0x00, offset at +0x04, cdOffset at +0x08,
        // hierarchy pointer at +0x10, and (usually) its own address at +0x14.
        List<Long> cols = new ArrayList<Long>();
        Memory mem = currentProgram.getMemory();
        for (MemoryBlock block : mem.getBlocks()) {
            if (!block.isRead()) {
                continue;
            }
            byte[] data = new byte[(int) Math.min(block.getSize(), 0x1000000L)];
            int read = mem.getBytes(block.getStart(), data);
            for (int i = 0; i + 4 <= read; i++) {
                long ptr = (data[i] & 0xffL) | ((data[i + 1] & 0xffL) << 8)
                        | ((data[i + 2] & 0xffL) << 16) | ((data[i + 3] & 0xffL) << 24);
                if (ptr != typeDescAbs) {
                    continue;
                }
                long from = block.getStart().getOffset() + i;
                long colAbs = from - 0x0c;
                out.append("candidate ptr to typeDesc at abs=0x")
                   .append(Long.toHexString(from)).append("\n");
                long sig = readU32(colAbs + 0x00);
                long off = readU32(colAbs + 0x04);
                long hier = readU32(colAbs + 0x10);
                long self = readU32(colAbs + 0x14);
                out.append("  col_candidate abs=0x").append(Long.toHexString(colAbs))
                   .append(" sig=").append(Long.toHexString(sig))
                   .append(" offset=").append(Long.toHexString(off))
                   .append(" hier=0x").append(Long.toHexString(hier))
                   .append(" self=0x").append(Long.toHexString(self)).append("\n");
                if (sig == 0 && hier != 0 && hier != -1L
                        && (self == colAbs || self == -1L || self == 0)) {
                    cols.add(colAbs);
                }
            }
        }
        out.append("complete_object_locators=").append(cols.size()).append("\n");
        for (Long col : cols) {
            out.append("  COL abs=0x").append(Long.toHexString(col))
               .append(" rva=0x").append(Long.toHexString(col - base)).append("\n");
        }

        // Pass 2: find vftables pointing back to any COL: *(vftable-4)==COL.
        // With -noanalysis there are no computed references, so scan readable
        // blocks for 4-byte pointers to each COL; the vftable base is the
        // pointer location + 4 (the COL pointer lives at vftable-4).
        out.append("\nvftables:\n");
        int shown = 0;
        for (MemoryBlock block : mem.getBlocks()) {
            if (!block.isRead()) {
                continue;
            }
            byte[] data = new byte[(int) Math.min(block.getSize(), 0x1000000L)];
            int read = mem.getBytes(block.getStart(), data);
            for (int i = 0; i + 4 <= read; i++) {
                long ptr = (data[i] & 0xffL) | ((data[i + 1] & 0xffL) << 8)
                        | ((data[i + 2] & 0xffL) << 16) | ((data[i + 3] & 0xffL) << 24);
                if (cols.contains(ptr) && i >= 4) {
                    long vftableAbs = block.getStart().getOffset() + i + 4;
                    out.append("  vftable abs=0x").append(Long.toHexString(vftableAbs))
                       .append(" rva=0x").append(Long.toHexString(vftableAbs - base))
                       .append(" col=0x").append(Long.toHexString(ptr)).append("\n");
                    shown++;
                    if (shown >= maxVftables) {
                        break;
                    }
                }
            }
            if (shown >= maxVftables) {
                break;
            }
        }
        out.append("vftable_count_shown=").append(shown).append("\n");

        String outPath = getEvidenceOutputPath("find-vftable-" + Long.toHexString(nameRva) + ".txt");
        PrintWriter w = new PrintWriter(new File(outPath), StandardCharsets.UTF_8);
        w.print(out);
        w.close();
        println("FOUND cols=" + cols.size() + " vftables=" + shown);
    }

    private long readU32(long absAddress) {
        try {
            byte[] b = new byte[4];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(absAddress), b);
            if (read != 4) return -1L;
            return (b[0] & 0xffL) | ((b[1] & 0xffL) << 8)
                    | ((b[2] & 0xffL) << 16) | ((b[3] & 0xffL) << 24);
        } catch (Exception e) {
            return -1L;
        }
    }

    private String readCString(long absAddress, int max) {
        try {
            byte[] b = new byte[max];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(absAddress), b);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < read; i++) {
                if (b[i] == 0) break;
                sb.append((char) (b[i] & 0xff));
            }
            return sb.toString();
        } catch (Exception e) {
            return "";
        }
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build" + File.separator + "ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException(
                    "Could not create Ghidra evidence directory");
        return new File(directory, fileName).getAbsolutePath();
    }
}
