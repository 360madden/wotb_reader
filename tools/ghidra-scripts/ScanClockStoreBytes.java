// ScanClockStoreBytes.java - exact byte-pattern scan for 8-byte Double
// stores whose effective displacement is 0x90:
//
//   FSTP m64fp [reg+0x90]:  DD 98|99|9A|9B|9E|9F 90 00 00 00   (disp32)
//                           DD 58|59|5A|5B|5E|5F 90            (disp8)
//   FST  m64fp [reg+0x90]:  DD 90|91|92|93|96|97 90 00 00 00
//   MOVSD [reg+0x90],XMMx:  F2 0F 11 80|88|90|98|A0|A8|B0|B8 90 00 00 00
//                           F2 0F 11 40|48|50|58|60|68|70|78 90  (disp8)
//   FADD/arith to [reg+0x90]: DC 80|...  90 00 00 00  (in-place update)
//   ADDSD [reg+0x90]:        F2 0F 58 80|... 90 00 00 00
//
// These are the only ways the replay clock (a Double at subobj+0x90) can be
// stored. Covers undecoded regions. Reports enclosing function when known.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class ScanClockStoreBytes extends GhidraScript {

    private final List<String> hits = new ArrayList<String>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Memory memory = currentProgram.getMemory();

        out.append("## byte scan: qword stores with displacement 0x90\n\n");
        int hitCount = 0;
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 8 < read; i++) {
                String tag = null;
                int len = 0;
                int b0 = data[i] & 0xff;
                int b1 = data[i + 1] & 0xff;
                int b2 = data[i + 2] & 0xff;
                int b3 = data[i + 3] & 0xff;
                int b4 = data[i + 4] & 0xff;

                // --- x87 m64fp stores with disp32 = 0x90 ---
                // FSTP m64fp: DD /3, mod=10 -> modrm 0x98|rm
                if (b0 == 0xdd && (b1 & 0xf8) == 0x98
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "FSTP m64fp [reg+0x90] (disp32)";
                    len = 6;
                }
                // FST m64fp: DD /2, mod=10 -> modrm 0x90|rm
                if (tag == null && b0 == 0xdd && (b1 & 0xf8) == 0x90
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "FST m64fp [reg+0x90] (disp32)";
                    len = 6;
                }
                // FSTP m64fp disp8: DD /3 mod=01 -> modrm 0x58|rm, disp8=90
                if (tag == null && b0 == 0xdd && (b1 & 0xf8) == 0x58
                        && b2 == (byte) 0x90) {
                    tag = "FSTP m64fp [reg+0x90] (disp8)";
                    len = 3;
                }
                // FST m64fp disp8: DD /2 mod=01 -> modrm 0x50|rm
                if (tag == null && b0 == 0xdd && (b1 & 0xf8) == 0x50
                        && b2 == (byte) 0x90) {
                    tag = "FST m64fp [reg+0x90] (disp8)";
                    len = 3;
                }
                // --- MOVSD qword store ---
                // F2 0F 11 /r mod=10 (disp32): modrm 0x80|rm (reg=000..111)
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x11 && (b3 & 0xc0) == 0x80
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "MOVSD [reg+0x90],XMM (disp32)";
                    len = 8;
                }
                // F2 0F 11 /r mod=01 (disp8): modrm 0x40|rm
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x11 && (b3 & 0xc0) == 0x40
                        && b4 == (byte) 0x90) {
                    tag = "MOVSD [reg+0x90],XMM (disp8)";
                    len = 5;
                }
                // MOVSD [reg+reg*scale+0x90],XMM (SIB, mod=10, rm=100)
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x11 && (b3 & 0xc7) == 0x84
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "MOVSD [SIB+0x90],XMM (disp32)";
                    len = 9;
                }
                // MOVSD [disp32],XMM (mod=00 rm=101)
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x11 && (b3 & 0xc7) == 0x05
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "MOVSD [disp32=0x90...],XMM (absolute)";
                    len = 8;
                }
                // --- MOVQ/MOVLPD/MOVHPD qword stores ---
                // MOVQ [reg+0x90],XMM: 66 0F D6 /r mod=10
                if (tag == null && b0 == 0x66 && b1 == 0x0f
                        && b2 == (byte) 0xd6 && (b3 & 0xc0) == 0x80
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "MOVQ [reg+0x90],XMM (disp32)";
                    len = 8;
                }
                // MOVLPD [reg+0x90],XMM: 66 0F 13 /r mod=10
                if (tag == null && b0 == 0x66 && b1 == 0x0f
                        && b2 == 0x13 && (b3 & 0xc0) == 0x80
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "MOVLPD [reg+0x90],XMM (disp32)";
                    len = 8;
                }
                // --- integer 8-byte stores via two 32-bit MOVs ---
                // MOV dword [reg+0x90],imm32 then MOV dword [reg+0x94],imm32
                // (MSVC split-double pattern): report the +0x90 half.
                if (tag == null && b0 == (byte) 0xc7 && (b1 & 0xc7) == 0x80
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "MOV dword [reg+0x90],imm32 (split-double candidate)";
                    len = 7;
                }
                // MOV dword [reg+0x90],reg (split-double low half)
                if (tag == null && b0 == (byte) 0x89 && (b1 & 0xc7) == 0x80
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "MOV dword [reg+0x90],reg (split-double candidate)";
                    len = 6;
                }
                // --- in-place arithmetic to [reg+0x90] ---
                // FADD m64fp [reg+0x90]: DC /0 mod=10 -> modrm 0x80|rm
                if (tag == null && b0 == (byte) 0xdc && (b1 & 0xf8) == 0x80
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "FADD m64fp [reg+0x90] (disp32, in-place)";
                    len = 6;
                }
                // FSUB m64fp [reg+0x90]: DC /4 mod=10 -> modrm 0xA0|rm
                if (tag == null && b0 == (byte) 0xdc && (b1 & 0xf8) == 0xa0
                        && b2 == (byte) 0x90 && b3 == 0 && b4 == 0
                        && data[i + 5] == 0) {
                    tag = "FSUB m64fp [reg+0x90] (disp32, in-place)";
                    len = 6;
                }
                // ADDSD [reg+0x90],XMM: F2 0F 58 /r mod=10
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x58 && (b3 & 0xc0) == 0x80
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "ADDSD [reg+0x90],XMM (disp32, in-place)";
                    len = 8;
                }
                // SUBSD [reg+0x90],XMM: F2 0F 5C /r mod=10
                if (tag == null && b0 == (byte) 0xf2 && b1 == 0x0f
                        && b2 == 0x5c && (b3 & 0xc0) == 0x80
                        && b4 == (byte) 0x90 && data[i + 5] == 0
                        && data[i + 6] == 0 && data[i + 7] == 0) {
                    tag = "SUBSD [reg+0x90],XMM (disp32, in-place)";
                    len = 8;
                }
                if (tag == null) {
                    continue;
                }
                long siteRva = block.getStart().add(i).getOffset()
                        - imageBase.getOffset();
                Address site = block.getStart().add(i);
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(site);
                String line = "site=0x" + Long.toHexString(siteRva)
                        + (fn == null ? " fn=<none>"
                                : " fn=0x" + Long.toHexString(
                                        fn.getEntryPoint().getOffset()
                                                - imageBase.getOffset())
                                + " " + fn.getName())
                        + " " + tag;
                hits.add(line);
                out.append(">>> ").append(line).append("\n");
                hitCount++;
                i += len - 1;
            }
        }

        out.append("\nhit_count=").append(hitCount).append("\n");
        writeReport(out.toString(), hitCount);
        println("VERDICT hits=" + hitCount);
    }

    private void writeReport(String body, int hits) throws Exception {
        String outPath = getEvidenceOutputPath("scan-clock-store-bytes.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.scan-clock-store-bytes.v2");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("scan=exact_qword_store_patterns_disp_0x90");
        w.println("hits=" + hits);
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
