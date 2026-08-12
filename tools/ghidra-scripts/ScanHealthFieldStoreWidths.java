// ScanHealthFieldStoreWidths.java - width-complete census of every store
// (8/16/32/64/128-bit, GPR + XMM) targeting the health int16 fields at
// entity-base +0xB8 (current HP) and +0x11E (healing). Complements
// FindHealthFieldStores.java (16-bit-only): the per-field atomicity claim
// (Branch A of the item-7 plan) requires that NO wider store spans the
// field, or the 16-bit reader could tear.
//
// Encodings covered (x86-64), all with full ModRM/SIB/disp parsing:
//   8-bit:  88 /r (reg->mem), C6 /0 (imm8)
//   16-bit: 66 89 /r, 66 C7 /0
//   32-bit: 89 /r, C7 /0
//   64-bit: REX.W 89 /r, REX.W C7 /0
//   XMM:    F3 0F 7F (movdqu m128), 66 0F 7F (movdqa m128),
//           0F 11 (movups m128), 0F 29 (movaps m128),
//           66 0F D6 (movq m64), F3 0F 7E (movq m64)
// Usage: -postScript ScanHealthFieldStoreWidths.java
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Program;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ScanHealthFieldStoreWidths extends GhidraScript {
    private static final int TARGET_B8 = 0xb8;
    private static final int TARGET_11E = 0x11e;

    private int[] count8 = new int[2];
    private int[] count16 = new int[2];
    private int[] count32 = new int[2];
    private int[] count64 = new int[2];
    private int[] count128 = new int[2];
    private List<String> non16 = new ArrayList<>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        out.append("## Store-width census at +0xB8 / +0x11E (health int16 fields)\n\n");
        Memory memory = currentProgram.getMemory();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) continue;
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) continue;
            for (int i = 0; i + 2 < read; i++) {
                int b = data[i] & 0xff;
                // REX prefix?
                if (b >= 0x40 && b <= 0x4f) {
                    boolean rexW = (b & 0x08) != 0;
                    int op = data[i + 1] & 0xff;
                    if (op == 0x66) { // 66 after REX.W: REX.W wins -> 64-bit
                        int op2 = data[i + 2] & 0xff;
                        if (op2 == 0x89 || op2 == 0xc7) {
                            int width = rexW ? 64 : 16;
                            if (matchStore(data, i + 2, op2, width, out, block, i)) i = skipTo(i, data, read);
                        } else if (op2 == 0x0f) {
                            int x = xmmWidth(data, i + 1);
                            if (x > 0 && matchStore(data, i + 2, 0x0f, x, out, block, i)) i = skipTo(i, data, read);
                        }
                    } else if (op == 0x89 || op == 0xc7) {
                        int width = rexW ? 64 : 32;
                        if (matchStore(data, i + 1, op, width, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x88 || op == 0xc6) {
                        if (matchStore(data, i + 1, op, 8, out, block, i)) i = skipTo(i, data, read);
                    }
                    continue;
                }
                // 66 prefix (16-bit operand, or 66 0F XMM store)
                if (b == 0x66) {
                    int op = data[i + 1] & 0xff;
                    if (op == 0x89 || op == 0xc7) {
                        if (matchStore(data, i + 1, op, 16, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x0f) {
                        int x = xmmWidth(data, i);
                        if (x > 0 && matchStore(data, i + 1, 0x0f, x, out, block, i)) i = skipTo(i, data, read);
                    }
                    continue;
                }
                // plain GPR stores
                if (b == 0x89 || b == 0xc7) {
                    if (matchStore(data, i, b, 32, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                if (b == 0x88 || b == 0xc6) {
                    if (matchStore(data, i, b, 8, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                // XMM stores (F3 0F 7F/7E, 0F 11/29)
                int x = xmmWidth(data, i);
                if (x > 0) {
                    int opStart = (data[i] & 0xff) == 0x0f ? i : i + 1;
                    if (matchStore(data, opStart, 0x0f, x, out, block, i)) i = skipTo(i, data, read);
                }
            }
        }
        if (!non16.isEmpty()) {
            out.append("non-16-bit sites (must be empty for per-field atomicity):\n");
            for (String s : non16) out.append("  ").append(s).append("\n");
            out.append("\n");
        }
        out.append("count_8_+0xb8=").append(count8[0]).append(" count_8_+0x11e=").append(count8[1]).append("\n");
        out.append("count_16_+0xb8=").append(count16[0]).append(" count_16_+0x11e=").append(count16[1]).append("\n");
        out.append("count_32_+0xb8=").append(count32[0]).append(" count_32_+0x11e=").append(count32[1]).append("\n");
        out.append("count_64_+0xb8=").append(count64[0]).append(" count_64_+0x11e=").append(count64[1]).append("\n");
        out.append("count_128_+0xb8=").append(count128[0]).append(" count_128_+0x11e=").append(count128[1]).append("\n");
        String outPath = getEvidenceOutputPath("scan-health-field-store-widths.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.scan-health-field-store-widths.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println();
        w.print(out);
        w.close();
        println("VERDICT 8=" + (count8[0] + count8[1]) + " 16=" + (count16[0] + count16[1])
                + " 32=" + (count32[0] + count32[1]) + " 64=" + (count64[0] + count64[1])
                + " 128=" + (count128[0] + count128[1]));
    }

    /** Advance past the store's ModRM+SIB+disp so we don't rescan its tail bytes. */
    private int skipTo(int i, byte[] data, int read) {
        return i + 4 < read ? i + 4 : i;
    }

    /** Recognize XMM store widths at i; returns 64 or 128, or 0 if not an XMM store. */
    private int xmmWidth(byte[] data, int i) {
        int b0 = data[i] & 0xff;
        int b1 = data[i + 1] & 0xff;
        int b2 = data[i + 2] & 0xff;
        if (b0 == 0xf3 && b1 == 0x0f && (b2 == 0x7f || b2 == 0x7e)) return b2 == 0x7f ? 128 : 64;
        if (b0 == 0x66 && b1 == 0x0f && (b2 == 0x7f || b2 == 0xd6)) return b2 == 0x7f ? 128 : 64;
        if (b0 == 0x0f && (b1 == 0x11 || b1 == 0x29)) return 128;
        return 0;
    }

    /**
     * Parse a store at data[idx] (opcode byte) and, if its effective address
     * displacement is +0xB8 or +0x11E, record the site.
     */
    private boolean matchStore(byte[] data, int idx, int op, int width, StringBuilder out,
            MemoryBlock block, int instrStart) {
        if (idx + 1 >= data.length) return false;
        int modrm = data[idx + 1] & 0xff;
        int mod = (modrm >> 6) & 3;
        if (mod == 3) return false; // register operand
        int reg = (modrm >> 3) & 7;
        int rm = modrm & 7;
        if ((op == 0xc7 || op == 0xc6) && reg != 0) return false; // C7/C6 /0 only
        int disp = 0;
        int next = idx + 2;
        boolean hasSib = rm == 4;
        if (hasSib) {
            if (next >= data.length) return false;
            int sib = data[next] & 0xff;
            int base = sib & 7;
            next++;
            if (mod == 0 && base == 5) {
                if (next + 3 >= data.length) return false;
                disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                     | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
                return record(disp, width, out, block, instrStart);
            }
            if (mod == 1) {
                if (next >= data.length) return false;
                disp = (data[next] << 24) >> 24; // sign-extend disp8
            } else if (mod == 2) {
                if (next + 3 >= data.length) return false;
                disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                     | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
            }
            return record(disp, width, out, block, instrStart);
        }
        if (mod == 0 && rm == 5) {
            // RIP-relative store (global write, not an object field) - not a health write
            return false;
        }
        if (mod == 1) {
            if (next >= data.length) return false;
            disp = (data[next] << 24) >> 24;
        } else if (mod == 2) {
            if (next + 3 >= data.length) return false;
            disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                 | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
        }
        return record(disp, width, out, block, instrStart);
    }

    private boolean record(int disp, int width, StringBuilder out, MemoryBlock block, int instrStart) {
        if (disp != TARGET_B8 && disp != TARGET_11E) return false;
        int slot = disp == TARGET_B8 ? 0 : 1;
        if (width == 8) count8[slot]++;
        else if (width == 16) count16[slot]++;
        else if (width == 32) count32[slot]++;
        else if (width == 64) count64[slot]++;
        else count128[slot]++;
        if (width != 16) {
            long rva = block.getStart().getOffset() + instrStart;
            Function fn = currentProgram.getFunctionManager().getFunctionContaining(
                    block.getStart().add(instrStart));
            String fnDesc = fn == null ? "?" : (fn.getName() + " @" + Long.toHexString(fn.getEntryPoint().getOffset()));
            non16.add("store=+0x" + Integer.toHexString(disp) + " width=" + width
                    + " site=0x" + Long.toHexString(rva) + " fn=" + fnDesc);
        }
        return true;
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build" + File.separator + "ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException("Could not create Ghidra evidence directory");
        return new File(directory, fileName).getAbsolutePath();
    }
}
