// ScanAvatarStatsQuadStoreWidths.java - width-complete census of EVERY write
// (8/16/32/64/128-bit, GPR + XMM; MOV stores AND in-place RMW — ADD/SUB/XOR/
// INC/DEC, because damageDealt INCREMENTS) targeting the Avatar battle-stats
// quad at entity-factory Avatar (vftable 0x36752a4) object offsets +0x118
// (dword0 = damageDealt), +0x11C (dword1 = damageBlocked), +0x120 (dword2 =
// damageAssisted1), +0x124 (dword3 = damageAssisted2). This is the item-7
// Branch A extension for the damage-dealt counter (the G2 publication draft's
// §7 note): the per-dword atomicity claim requires that NO write wider than
// the uint32 semantic (and in particular no UNALIGNED 64/128-bit write at
// +0x11C/+0x120, which would NOT be atomic on x86) touches any quad dword.
//
// The census matches by DISPLACEMENT ONLY ([reg + 0x118..0x124]) — sites may
// belong to other object families with the same field offsets; classification
// requires function context (printed per site) and is bounded live by the
// OD-RECOVERY-095/096 increment correlation (exact dword0 steps at the right
// times on the actual object).
//
// Encodings covered (x86-64), all with full ModRM/SIB/disp parsing (mirrors
// ScanHealthFieldStoreWidths.java + the RMW families):
//   MOV:   88 /r (8), C6 /0 (imm8), 89 /r (32), C7 /0 (imm32),
//          66 89 /r (16), 66 C7 /0 (imm16), REX.W 89/C7 (64)
//   RMW:   00/28/30 (8-bit ADD/SUB/XOR r/m8,r8),
//          01/29/31 (32-bit ADD/SUB/XOR r/m32,r32; 66 -> 16; REX.W -> 64),
//          80/81/83 group-1 (imm8/imm32/imm8 by /digit: 0=ADD 5=SUB 6=XOR),
//          FE /0,/1 (8-bit INC/DEC), FF /0,/1 (32-bit INC/DEC; REX.W -> 64),
//          0F C1 (XADD r/m,r; 66->16, REX.W->64), 0F B1 (CMPXCHG r/m,r) - the
//          atomic/in-place register-source forms a variable damage amount
//          could take
//   XMM:   F3 0F 7F (movdqu m128), 66 0F 7F (movdqa m128),
//          0F 11 (movups m128), 0F 29 (movaps m128),
//          66 0F D6 (movq m64), F3 0F 7E (movq m64)
// Usage: -postScript ScanAvatarStatsQuadStoreWidths.java
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ScanAvatarStatsQuadStoreWidths extends GhidraScript {
    private static final int[] TARGETS = { 0x118, 0x11c, 0x120, 0x124 };

    private int[] count8 = new int[4];
    private int[] count16 = new int[4];
    private int[] count32 = new int[4];
    private int[] count64 = new int[4];
    private int[] count128 = new int[4];
    private int[] rmw32 = new int[4]; // 32-bit in-place RMW (ADD/SUB/XOR/INC/DEC)
    private List<String> sites = new ArrayList<>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        out.append("## Store-width census at +0x118/+0x11C/+0x120/+0x124 (Avatar battle-stats quad)\n\n");
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
                            else if (i + 3 < read && (data[i + 3] == 0xc1 || data[i + 3] == 0xb1)) {
                                if (matchRmw(data, i + 3, data[i + 3], rexW ? 64 : 16, out, block, i)) i = skipTo(i, data, read);
                            }
                        } else if (op2 == 0x01 || op2 == 0x29 || op2 == 0x31) {
                            if (matchRmw(data, i + 2, op2, rexW ? 64 : 16, out, block, i)) i = skipTo(i, data, read);
                        } else if (op2 == 0xff) {
                            if (matchGroup(data, i + 2, op2, rexW ? 64 : 16, out, block, i)) i = skipTo(i, data, read);
                        } else if (op2 == 0x81 || op2 == 0x83) {
                            if (matchGroup1(data, i + 2, op2, rexW ? 64 : 16, out, block, i)) i = skipTo(i, data, read);
                        }
                    } else if (op == 0x89 || op == 0xc7) {
                        int width = rexW ? 64 : 32;
                        if (matchStore(data, i + 1, op, width, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x88 || op == 0xc6) {
                        if (matchStore(data, i + 1, op, 8, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x01 || op == 0x29 || op == 0x31) {
                        if (matchRmw(data, i + 1, op, rexW ? 64 : 32, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0xff) {
                        if (matchGroup(data, i + 1, op, rexW ? 64 : 32, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x81 || op == 0x83) {
                        if (matchGroup1(data, i + 1, op, rexW ? 64 : 32, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x0f) {
                        if (i + 2 < read && (data[i + 2] == 0xc1 || data[i + 2] == 0xb1)) {
                            if (matchRmw(data, i + 2, data[i + 2], rexW ? 64 : 32, out, block, i)) i = skipTo(i, data, read);
                        }
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
                    } else if (op == 0x01 || op == 0x29 || op == 0x31) {
                        if (matchRmw(data, i + 1, op, 16, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0xff) {
                        if (matchGroup(data, i + 1, op, 16, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x81 || op == 0x83) {
                        if (matchGroup1(data, i + 1, op, 16, out, block, i)) i = skipTo(i, data, read);
                    } else if (op == 0x0f) {
                        if (i + 2 < read && (data[i + 2] == 0xc1 || data[i + 2] == 0xb1)) {
                            if (matchRmw(data, i + 2, data[i + 2], 16, out, block, i)) i = skipTo(i, data, read);
                        }
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
                // in-place RMW writes (damageDealt INCREMENTS - the MOV-only
                // census would miss the live write path)
                if (b == 0x01 || b == 0x29 || b == 0x31) { // ADD/SUB/XOR r/m32,r32
                    if (matchRmw(data, i, b, 32, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                if (b == 0x00 || b == 0x28 || b == 0x30) { // 8-bit ADD/SUB/XOR r/m8,r8
                    if (matchRmw(data, i, b, 8, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                if (b == 0xff) { // group 5: /0 INC r/m32 (r/m64 with REX.W), /1 DEC
                    if (matchGroup(data, i, 0xff, 32, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                if (b == 0xfe) { // group 4: /0 INC r/m8, /1 DEC r/m8
                    if (matchGroup8(data, i, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                if (b == 0x80 || b == 0x81 || b == 0x83) { // group 1 imm (ADD/SUB/XOR by /digit)
                    if (matchGroup1(data, i, b, 32, out, block, i)) i = skipTo(i, data, read);
                    continue;
                }
                // XMM stores (F3 0F 7F/7E, 0F 11/29)
                int x = xmmWidth(data, i);
                if (x > 0) {
                    int opStart = (data[i] & 0xff) == 0x0f ? i : i + 1;
                    if (matchStore(data, opStart, 0x0f, x, out, block, i)) i = skipTo(i, data, read);
                } else if (b == 0x0f && i + 2 < read && (data[i + 1] == 0xc1 || data[i + 1] == 0xb1)) {
                    // XADD / CMPXCHG r/m32,r32 (memory dest)
                    if (matchRmw(data, i + 1, data[i + 1], 32, out, block, i)) i = skipTo(i, data, read);
                }
            }
        }
        out.append("ALL sites (width + RVA + containing function):\n");
        for (String s : sites) out.append("  ").append(s).append("\n");
        out.append("\n");
        String[] dwords = { "d0_+0x118", "d1_+0x11c", "d2_+0x120", "d3_+0x124" };
        for (int s = 0; s < 4; s++) {
            out.append("count_8_" + dwords[s] + "=" + count8[s] + "\n");
            out.append("count_16_" + dwords[s] + "=" + count16[s] + "\n");
            out.append("count_32_" + dwords[s] + "=" + count32[s] + "\n");
            out.append("count_32_rmw_" + dwords[s] + "=" + rmw32[s] + "\n");
            out.append("count_64_" + dwords[s] + "=" + count64[s] + "\n");
            out.append("count_128_" + dwords[s] + "=" + count128[s] + "\n");
        }
        String outPath = getEvidenceOutputPath("scan-avatar-stats-quad-store-widths.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.scan-avatar-stats-quad-store-widths.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println();
        w.print(out);
        w.close();
        println("VERDICT 8=" + sum(count8) + " 16=" + sum(count16) + " 32=" + sum(count32)
                + " 64=" + sum(count64) + " 128=" + sum(count128));
    }

    private int sum(int[] a) {
        int t = 0;
        for (int v : a) t += v;
        return t;
    }

    /** Advance past the store's ModRM+SIB+disp so we don't rescan its tail bytes. */
    private int skipTo(int i, byte[] data, int read) {
        return i + 4 < read ? i + 4 : i;
    }

    /** RMW ADD/SUB/XOR reg->mem at the given width. */
    private boolean matchRmw(byte[] data, int idx, int op, int width, StringBuilder out,
            MemoryBlock block, int instrStart) {
        return matchGeneric(data, idx, op, width, true, out, block, instrStart);
    }

    /** group 5: FF /0 INC /1 DEC at the given width (32 default, 64 REX.W, 16 66). */
    private boolean matchGroup(byte[] data, int idx, int op, int width, StringBuilder out,
            MemoryBlock block, int instrStart) {
        if (idx + 1 >= data.length) return false;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 1) return false; // FF /2..7 are CALL/JMP/PUSH (not writes)
        return matchGeneric(data, idx, op, width, true, out, block, instrStart);
    }

    /** group 4: FE /0 INC r/m8, /1 DEC r/m8. */
    private boolean matchGroup8(byte[] data, int idx, StringBuilder out,
            MemoryBlock block, int instrStart) {
        if (idx + 1 >= data.length) return false;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 1) return false;
        return matchGeneric(data, idx, 0xfe, 8, true, out, block, instrStart);
    }

    /** group 1 with immediate: 80 (imm8 byte), 81 (imm32/imm64 by width, imm16 with
     *  66), 83 (imm8 at the given width). Only ADD(/0), SUB(/5), XOR(/6). */
    private boolean matchGroup1(byte[] data, int idx, int op, int width, StringBuilder out,
            MemoryBlock block, int instrStart) {
        if (idx + 1 >= data.length) return false;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 5 && digit != 6) return false;
        int w = op == 0x80 ? 8 : width;
        return matchGeneric(data, idx, op, w, true, out, block, instrStart);
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
     * displacement is one of +0x118/+0x11C/+0x120/+0x124, record the site
     * (width + RVA + containing function) and bump the width count for the
     * matching dword slot.
     */
    private boolean matchStore(byte[] data, int idx, int op, int width, StringBuilder out,
            MemoryBlock block, int instrStart) {
        return matchGeneric(data, idx, op, width, false, out, block, instrStart);
    }

    /**
     * Shared ModRM/SIB/disp parser for MOV stores and RMW writes. rmw=true
     * tags the site as an in-place increment (ADD/SUB/XOR/INC/DEC) so the
     * write-path classification can distinguish it from a MOV store.
     */
    private boolean matchGeneric(byte[] data, int idx, int op, int width, boolean rmw,
            StringBuilder out, MemoryBlock block, int instrStart) {
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
                return record(disp, width, rmw, block, instrStart);
            }
            if (mod == 1) {
                if (next >= data.length) return false;
                disp = (data[next] << 24) >> 24; // sign-extend disp8
            } else if (mod == 2) {
                if (next + 3 >= data.length) return false;
                disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                     | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
            }
            return record(disp, width, rmw, block, instrStart);
        }
        if (mod == 0 && rm == 5) {
            // RIP-relative (global write, not an object field) - not a quad write
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
        return record(disp, width, rmw, block, instrStart);
    }

    private boolean record(int disp, int width, boolean rmw, MemoryBlock block, int instrStart) {
        int slot = -1;
        for (int s = 0; s < 4; s++) {
            if (disp == TARGETS[s]) { slot = s; break; }
        }
        if (slot < 0) return false;
        if (width == 8) count8[slot]++;
        else if (width == 16) count16[slot]++;
        else if (width == 32) count32[slot]++;
        else if (width == 64) count64[slot]++;
        else count128[slot]++;
        if (rmw && width == 32) rmw32[slot]++;
        long rva = block.getStart().add(instrStart).getOffset()
                - currentProgram.getImageBase().getOffset();
        Function fn = currentProgram.getFunctionManager().getFunctionContaining(
                block.getStart().add(instrStart));
        String fnDesc = fn == null ? "?" : (fn.getName() + " @0x"
                + Long.toHexString(fn.getEntryPoint().getOffset()));
        sites.add("store=+0x" + Integer.toHexString(disp) + " width=" + width
                + (rmw ? " rmw=true" : " rmw=false")
                + " site=0x" + Long.toHexString(rva) + " fn=" + fnDesc);
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
