// ConfirmHealthFieldStores.java - confirmation pass for the byte-scan
// candidates: an unaligned raw-byte walker CANNOT prove write-sites (it
// scans through instruction interiors, so random bytes inside
// displacements/immediates parse as fake stores - see the 360 "32-bit"
// hits in ScanHealthFieldStoreWidths). The honest check is whether each
// candidate RVA sits at a REAL instruction boundary in the analyzed
// listing. Candidates that are not instruction starts are false positives
// by construction; candidates that are get their true instruction text +
// operand size recorded.
//
// Re-derives candidates with the same encodings as the byte scans, then
// confirms each against currentProgram.getListing().
// Usage: -postScript ConfirmHealthFieldStores.java
import java.io.File;
import java.io.PrintWriter;
import java.util.Set;
import java.util.TreeSet;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ConfirmHealthFieldStores extends GhidraScript {
    private static final int TARGET_B8 = 0xb8;
    private static final int TARGET_11E = 0x11e;
    private Set<Long> candidates = new TreeSet<>();
    private Set<Long> confirmed = new TreeSet<>();
    private Set<Long> notBoundary = new TreeSet<>();

    @Override
    public void run() throws Exception {
        scanCandidates();
        StringBuilder out = new StringBuilder();
        out.append("## Listing-confirmed stores to +0xB8 / +0x11E\n\n");
        int nConfirmed = 0;
        Address imageBase = currentProgram.getImageBase();
        for (long rva : candidates) {
            Address addr = imageBase.add(rva);
            Instruction insn = currentProgram.getListing().getInstructionAt(addr);
            if (insn == null) {
                notBoundary.add(rva);
                continue;
            }
            confirmed.add(rva);
            nConfirmed++;
            out.append("site=0x").append(Long.toHexString(rva))
               .append(" op0=").append(insn.getDefaultOperandRepresentation(0))
               .append(" text=").append(insn.toString().replace("\n", " ")).append("\n");
        }
        out.append("\ncandidates=").append(candidates.size())
           .append(" confirmed_at_instruction_boundary=").append(nConfirmed)
           .append(" not_instruction_boundary=").append(notBoundary.size()).append("\n");
        String outPath = getEvidenceOutputPath("confirm-health-field-stores.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.confirm-health-field-stores.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println();
        w.print(out);
        w.close();
        println("VERDICT confirmed=" + nConfirmed + " falsepos=" + notBoundary.size());
    }

    /** Unaligned byte walk collecting every store encoding targeting +0xB8/+0x11E. */
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
                        if (op2 == 0x89 || op2 == 0xc7 || op2 == 0x0f) tryCandidate(data, i + 2, op2, i, block);
                    } else if (op == 0x89 || op == 0xc7 || op == 0x88 || op == 0xc6) {
                        tryCandidate(data, i + 1, op, i, block);
                    }
                    continue;
                }
                if (b == 0x66) {
                    int op = data[i + 1] & 0xff;
                    if (op == 0x89 || op == 0xc7) tryCandidate(data, i + 1, op, i, block);
                    else if (op == 0x0f) tryCandidate(data, i + 1, 0x0f, i, block);
                    continue;
                }
                if (b == 0x89 || b == 0xc7 || b == 0x88 || b == 0xc6) tryCandidate(data, i, b, i, block);
                int x = xmmWidth(data, i);
                if (x > 0) tryCandidate(data, (b == 0x0f) ? i : i + 1, 0x0f, i, block);
            }
        }
    }

    private void tryCandidate(byte[] data, int idx, int op, int seqStart, MemoryBlock block) {
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
                record(block, seqStart, readDisp32(data, next));
            } else if (mod == 1) {
                if (next >= data.length) return;
                record(block, seqStart, (data[next] << 24) >> 24);
            } else if (mod == 2) {
                if (next + 3 >= data.length) return;
                record(block, seqStart, readDisp32(data, next));
            }
            return;
        }
        if (mod == 0 && rm == 5) return; // RIP-relative
        if (mod == 1) {
            if (next >= data.length) return;
            record(block, seqStart, (data[next] << 24) >> 24);
        } else if (mod == 2) {
            if (next + 3 >= data.length) return;
            record(block, seqStart, readDisp32(data, next));
        }
    }

    private int readDisp32(byte[] data, int next) {
        return (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
             | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
    }

    private void record(MemoryBlock block, int seqStart, int disp) {
        if (disp != TARGET_B8 && disp != TARGET_11E) return;
        candidates.add(block.getStart().add(seqStart).getOffset() - currentProgram.getImageBase().getOffset());
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
