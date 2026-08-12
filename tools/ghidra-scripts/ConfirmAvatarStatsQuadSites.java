// ConfirmAvatarStatsQuadSites.java - confirmation pass for the Avatar
// battle-stats quad store census (ScanAvatarStatsQuadStoreWidths.java): an
// unaligned raw-byte walker CANNOT prove write-sites (it scans through
// instruction interiors, so random bytes inside displacements/immediates
// parse as fake stores - e.g. the ADD ESP,0x4 false positive at 0x2451db7).
// The honest check mirrors ConfirmHealthFieldStores.java: each candidate RVA
// must sit at a REAL instruction boundary in the analyzed listing; confirmed
// sites get their true instruction text + claimed width recorded.
//
// Encodings covered (must match the census): MOV 88/C6 (8), 89/C7 (32),
// 66 89/C7 (16), REX.W 89/C7 (64), XMM stores; RMW 00/28/30 (8), 01/29/31
// (32; 66->16, REX.W->64), 80/81/83 group-1 imm (ADD/SUB/XOR), FE group-4
// (8-bit INC/DEC), FF group-5 (INC/DEC).
// Usage: -postScript ConfirmAvatarStatsQuadSites.java
import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.TreeSet;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ConfirmAvatarStatsQuadSites extends GhidraScript {
    private static final int[] TARGETS = { 0x118, 0x11c, 0x120, 0x124 };

    /** candidate RVA -> {claimed width, rmw, slot 0..3} */
    private Map<Long, int[]> candidates = new LinkedHashMap<>();
    private TreeSet<Long> confirmed = new TreeSet<>();
    private TreeSet<Long> notBoundary = new TreeSet<>();

    @Override
    public void run() throws Exception {
        scanCandidates();
        StringBuilder out = new StringBuilder();
        out.append("## Listing-confirmed writes to +0x118/+0x11C/+0x120/+0x124 (Avatar battle-stats quad)\n\n");
        int[] w8 = new int[4], w16 = new int[4], w32 = new int[4], w64 = new int[4], w128 = new int[4];
        int[] rmw = new int[4];
        int nonReal = 0;
        Address imageBase = currentProgram.getImageBase();
        for (Map.Entry<Long, int[]> e : candidates.entrySet()) {
            long rva = e.getKey();
            int claimedWidth = e.getValue()[0];
            boolean isRmw = e.getValue()[1] != 0;
            Address addr = imageBase.add(rva);
            Instruction insn = currentProgram.getListing().getInstructionAt(addr);
            if (insn == null) {
                notBoundary.add(rva);
                continue;
            }
            confirmed.add(rva);
            String text = insn.toString().replace("\n", " ");
            // SEMANTIC FILTER: a real quad write must have a memory operand
            // ([base + 0xNNN]) whose displacement is one of the targets. The
            // byte scan can misattribute register-only instructions (e.g.
            // INC EAX / DEC EAX) that happen to sit at a boundary - the
            // boundary check alone is NOT sufficient (see 0x2052204 DEC EAX).
            int trueSlot = memDisplacementSlot(text);
            if (trueSlot < 0 || !text.contains("ptr [")) {
                nonReal++;
                out.append("site=0x").append(Long.toHexString(rva))
                   .append(" claimed_width=").append(claimedWidth)
                   .append(isRmw ? " rmw=true" : " rmw=false")
                   .append(" NOT_A_MEM_WRITE text=").append(text).append("\n");
                continue;
            }
            int trueWidth = textWidth(text);
            int slot = trueSlot;
            if (trueWidth == 8) w8[slot]++;
            else if (trueWidth == 16) w16[slot]++;
            else if (trueWidth == 32) w32[slot]++;
            else if (trueWidth == 64) w64[slot]++;
            else w128[slot]++;
            if (isRmw && trueWidth == 32) rmw[slot]++;
            out.append("site=0x").append(Long.toHexString(rva))
               .append(" claimed_width=").append(claimedWidth)
               .append(isRmw ? " rmw=true" : " rmw=false")
               .append(" true_width=").append(trueWidth)
               .append(" text=").append(text).append("\n");
        }
        out.append("\ncandidates=").append(candidates.size())
           .append(" confirmed_at_instruction_boundary=").append(confirmed.size())
           .append(" not_instruction_boundary=").append(notBoundary.size())
           .append(" not_a_real_mem_write=").append(nonReal).append("\n\n");
        String[] dwords = { "d0_+0x118", "d1_+0x11c", "d2_+0x120", "d3_+0x124" };
        for (int s = 0; s < 4; s++) {
            out.append("confirmed_8_" + dwords[s] + "=" + w8[s] + "\n");
            out.append("confirmed_16_" + dwords[s] + "=" + w16[s] + "\n");
            out.append("confirmed_32_" + dwords[s] + "=" + w32[s] + "\n");
            out.append("confirmed_32_rmw_" + dwords[s] + "=" + rmw[s] + "\n");
            out.append("confirmed_64_" + dwords[s] + "=" + w64[s] + "\n");
            out.append("confirmed_128_" + dwords[s] + "=" + w128[s] + "\n");
        }
        String outPath = getEvidenceOutputPath("confirm-avatar-stats-quad-sites.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.confirm-avatar-stats-quad-sites.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println();
        w.print(out);
        w.close();
        println("VERDICT candidates=" + candidates.size() + " confirmed=" + confirmed.size()
                + " falsepos=" + notBoundary.size());
    }

    /** Which quad dword a memory operand [base + 0xNNN] targets, from its text. */
    private int memDisplacementSlot(String text) {
        int open = text.indexOf('[');
        int close = text.indexOf(']');
        if (open < 0 || close < 0 || close <= open) return -1;
        String inner = text.substring(open, close);
        int lastHex = inner.lastIndexOf("0x");
        if (lastHex < 0) return -1;
        int val = 0;
        try {
            val = Integer.decode(inner.substring(lastHex).trim());
        } catch (NumberFormatException ex) {
            return -1;
        }
        for (int s = 0; s < 4; s++) {
            if (val == TARGETS[s]) return s;
        }
        return -1;
    }

    /** True operand width from the instruction text (byte/word/dword/qword ptr). */
    private int textWidth(String text) {
        if (text.contains("qword ptr")) return 64;
        if (text.contains("dword ptr")) return 32;
        if (text.contains("word ptr")) return 16;
        if (text.contains("byte ptr")) return 8;
        // RMW without a size qualifier (INC/DEC r/m32 default)
        return 32;
    }

    /** Unaligned byte walk collecting every MOV/RMW write encoding targeting the quad offsets. */
    private void scanCandidates() throws Exception {
        Memory memory = currentProgram.getMemory();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) continue;
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) continue;
            for (int i = 0; i + 2 < read; i++) {
                int b = data[i] & 0xff;
                if (b >= 0x40 && b <= 0x4f) { // REX
                    boolean rexW = (b & 0x08) != 0;
                    int op = data[i + 1] & 0xff;
                    if (op == 0x66) {
                        int op2 = data[i + 2] & 0xff;
                        if (op2 == 0x89 || op2 == 0xc7) {
                            tryCandidate(data, i + 2, op2, rexW ? 64 : 16, false, i, block);
                        } else if (op2 == 0x0f) {
                            int x = xmmWidth(data, i + 1);
                            if (x > 0) tryCandidate(data, i + 2, 0x0f, x, false, i, block);
                            else if (i + 3 < read && (data[i + 3] == 0xc1 || data[i + 3] == 0xb1)) {
                                tryCandidate(data, i + 3, data[i + 3], rexW ? 64 : 16, true, i, block);
                            }
                        } else if (op2 == 0x01 || op2 == 0x29 || op2 == 0x31) {
                            tryCandidate(data, i + 2, op2, rexW ? 64 : 16, true, i, block);
                        } else if (op2 == 0xff) {
                            tryGroup(data, i + 2, 0xff, rexW ? 64 : 16, i, block);
                        } else if (op2 == 0x81 || op2 == 0x83) {
                            tryGroup1(data, i + 2, op2, rexW ? 64 : 16, i, block);
                        }
                    } else if (op == 0x89 || op == 0xc7) {
                        tryCandidate(data, i + 1, op, rexW ? 64 : 32, false, i, block);
                    } else if (op == 0x88 || op == 0xc6) {
                        tryCandidate(data, i + 1, op, 8, false, i, block);
                    } else if (op == 0x01 || op == 0x29 || op == 0x31) {
                        tryCandidate(data, i + 1, op, rexW ? 64 : 32, true, i, block);
                    } else if (op == 0xff) {
                        tryGroup(data, i + 1, 0xff, rexW ? 64 : 32, i, block);
                    } else if (op == 0x81 || op == 0x83) {
                        tryGroup1(data, i + 1, op, rexW ? 64 : 32, i, block);
                    } else if (op == 0x0f) {
                        if (i + 2 < read && (data[i + 2] == 0xc1 || data[i + 2] == 0xb1)) {
                            tryCandidate(data, i + 2, data[i + 2], rexW ? 64 : 32, true, i, block);
                        }
                    }
                    continue;
                }
                if (b == 0x66) {
                    int op = data[i + 1] & 0xff;
                    if (op == 0x89 || op == 0xc7) {
                        tryCandidate(data, i + 1, op, 16, false, i, block);
                    } else if (op == 0x0f) {
                        int x = xmmWidth(data, i);
                        if (x > 0) tryCandidate(data, i + 1, 0x0f, x, false, i, block);
                    } else if (op == 0x01 || op == 0x29 || op == 0x31) {
                        tryCandidate(data, i + 1, op, 16, true, i, block);
                    } else if (op == 0xff) {
                        tryGroup(data, i + 1, 0xff, 16, i, block);
                    } else if (op == 0x81 || op == 0x83) {
                        tryGroup1(data, i + 1, op, 16, i, block);
                    } else if (op == 0x0f) {
                        if (i + 2 < read && (data[i + 2] == 0xc1 || data[i + 2] == 0xb1)) {
                            tryCandidate(data, i + 2, data[i + 2], 16, true, i, block);
                        }
                    }
                    continue;
                }
                if (b == 0x89 || b == 0xc7) {
                    tryCandidate(data, i, b, 32, false, i, block);
                    continue;
                }
                if (b == 0x88 || b == 0xc6) {
                    tryCandidate(data, i, b, 8, false, i, block);
                    continue;
                }
                if (b == 0x01 || b == 0x29 || b == 0x31) {
                    tryCandidate(data, i, b, 32, true, i, block);
                    continue;
                }
                if (b == 0x00 || b == 0x28 || b == 0x30) {
                    tryCandidate(data, i, b, 8, true, i, block);
                    continue;
                }
                if (b == 0xff) {
                    tryGroup(data, i, 0xff, 32, i, block);
                    continue;
                }
                if (b == 0xfe) {
                    tryGroup8(data, i, i, block);
                    continue;
                }
                if (b == 0x80 || b == 0x81 || b == 0x83) {
                    tryGroup1(data, i, b, 32, i, block);
                    continue;
                }
                int x = xmmWidth(data, i);
                if (x > 0) tryCandidate(data, (b == 0x0f) ? i : i + 1, 0x0f, x, false, i, block);
                else if (b == 0x0f && i + 2 < read && (data[i + 1] == 0xc1 || data[i + 1] == 0xb1)) {
                    tryCandidate(data, i + 1, data[i + 1], 32, true, i, block);
                }
            }
        }
    }

    private void tryGroup(byte[] data, int idx, int op, int width, int seqStart, MemoryBlock block) {
        if (idx + 1 >= data.length) return;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 1) return;
        tryCandidate(data, idx, op, width, true, seqStart, block);
    }

    private void tryGroup8(byte[] data, int idx, int seqStart, MemoryBlock block) {
        if (idx + 1 >= data.length) return;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 1) return;
        tryCandidate(data, idx, 0xfe, 8, true, seqStart, block);
    }

    private void tryGroup1(byte[] data, int idx, int op, int width, int seqStart, MemoryBlock block) {
        if (idx + 1 >= data.length) return;
        int modrm = data[idx + 1] & 0xff;
        int digit = (modrm >> 3) & 7;
        if (digit != 0 && digit != 5 && digit != 6) return;
        tryCandidate(data, idx, op, op == 0x80 ? 8 : width, true, seqStart, block);
    }

    private void tryCandidate(byte[] data, int idx, int op, int width, boolean rmw,
            int seqStart, MemoryBlock block) {
        if (idx + 1 >= data.length) return;
        int modrm = data[idx + 1] & 0xff;
        int mod = (modrm >> 6) & 3;
        if (mod == 3) return;
        int reg = (modrm >> 3) & 7;
        int rm = modrm & 7;
        if ((op == 0xc7 || op == 0xc6) && reg != 0) return;
        int next = idx + 2;
        boolean hasSib = rm == 4;
        if (hasSib) {
            if (next >= data.length) return;
            int base = data[next] & 7;
            next++;
            if (mod == 0 && base == 5) {
                if (next + 3 >= data.length) return;
                record(block, seqStart, width, rmw, readDisp32(data, next));
            } else if (mod == 1) {
                if (next >= data.length) return;
                record(block, seqStart, width, rmw, (data[next] << 24) >> 24);
            } else if (mod == 2) {
                if (next + 3 >= data.length) return;
                record(block, seqStart, width, rmw, readDisp32(data, next));
            }
            return;
        }
        if (mod == 0 && rm == 5) return; // RIP-relative
        if (mod == 1) {
            if (next >= data.length) return;
            record(block, seqStart, width, rmw, (data[next] << 24) >> 24);
        } else if (mod == 2) {
            if (next + 3 >= data.length) return;
            record(block, seqStart, width, rmw, readDisp32(data, next));
        }
    }

    private int readDisp32(byte[] data, int next) {
        return (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
             | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
    }

    private void record(MemoryBlock block, int seqStart, int width, boolean rmw, int disp) {
        int slot = -1;
        for (int s = 0; s < 4; s++) {
            if (disp == TARGETS[s]) { slot = s; break; }
        }
        if (slot < 0) return;
        long rva = block.getStart().add(seqStart).getOffset() - currentProgram.getImageBase().getOffset();
        // Keep the FIRST claim per RVA (the parser can double-fire on one byte);
        // width is only meaningful after the boundary confirmation anyway.
        if (!candidates.containsKey(rva)) {
            candidates.put(rva, new int[] { width, rmw ? 1 : 0, slot });
        }
    }

    private int xmmWidth(byte[] data, int i) {
        int b0 = data[i] & 0xff;
        int b1 = data[i + 1] & 0xff;
        int b2 = data[i + 2] & 0xff;
        if (b0 == 0xf3 && b1 == 0x0f && (b2 == 0x7f || b2 == 0x7e)) return b2 == 0x7f ? 128 : 64;
        if (b0 == 0x66 && b1 == 0x0f && (b2 == 0x7f || b2 == 0xd6)) return b2 == 0x7f ? 128 : 64;
        if (b0 == 0x0f && (b1 == 0x11 || b1 == 0x29)) return 128;
        return 0;
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
