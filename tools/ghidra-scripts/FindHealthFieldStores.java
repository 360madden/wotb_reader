// FindHealthFieldStores.java - locate 16-bit (WORD) stores to +0xB8 and
// +0x11E on any register (the VehicleGameLogic health / healingHealth
// fields). set_health reads [entity+0xB8] as int16 and compares <1 for
// death; set_healingHealth reads [entity+0x11E]. The WRITE site is the
// damage-application path (type-32 handler or property sync), which the L1
// session can use as its HP anchor proof.
//
// Encodings covered (x86):
//   MOV word [reg+disp8], r16   -> 66 89 /r (ModRM rm=reg, disp8)
//   MOV word [reg+disp32], r16  -> 66 89 /r (ModRM rm=reg, disp32)
//   MOV word [reg+disp8], imm16 -> 66 C7 /0 (ModRM rm=reg, disp8)
//   MOV word [reg+disp32], imm16-> 66 C7 /0 (ModRM rm=reg, disp32)
//   MOV word [reg+disp8], m16   -> 66 8B load+store handled by caller
// Usage: -postScript FindHealthFieldStores.java
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class FindHealthFieldStores extends GhidraScript {
    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        StringBuilder out = new StringBuilder();
        out.append("## 16-bit stores to +0xB8 / +0x11E (health fields)\n\n");
        int[] targets = { 0xb8, 0x11e };
        int[] counts = new int[targets.length];

        Memory memory = currentProgram.getMemory();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) continue;
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) continue;
            for (int i = 0; i + 1 < read; i++) {
                // need 66 prefix (operand-size) then 89/C7 with ModRM
                if ((data[i] & 0xff) != 0x66) continue;
                int op = data[i + 1] & 0xff;
                if (op != 0x89 && op != 0xc7) continue;
                if (i + 2 >= read) continue;
                int modrm = data[i + 2] & 0xff;
                int mod = (modrm >> 6) & 3;
                if (mod == 3) continue;             // register operand
                int reg = (modrm >> 3) & 7;
                if (op == 0xc7 && reg != 0) continue; // C7 /0 only
                int rm = modrm & 7;
                // displacement
                int disp = 0;
                int next = i + 3;
                if (mod == 0) {
                    if (rm == 5) { // disp32
                        if (next + 3 >= read) continue;
                        disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                             | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
                        next += 4;
                    } else if (rm == 4) {
                        // SIB with mod=00: base in SIB; skip (rare for our fields)
                        continue;
                    }
                } else if (mod == 1) {
                    if (next >= read) continue;
                    disp = (byte) data[next];
                    next += 1;
                } else if (mod == 2) {
                    if (next + 3 >= read) continue;
                    disp = (data[next] & 0xff) | ((data[next + 1] & 0xff) << 8)
                         | ((data[next + 2] & 0xff) << 16) | ((data[next + 3] & 0xff) << 24);
                    next += 4;
                }
                for (int t = 0; t < targets.length; t++) {
                    if (disp != targets[t]) continue;
                    long siteRva = block.getStart().add(i).getOffset() - imageBase.getOffset();
                    Function fn = currentProgram.getFunctionManager()
                            .getFunctionContaining(block.getStart().add(i));
                    out.append("store=+0x").append(Integer.toHexString(disp))
                       .append(" site=0x").append(Long.toHexString(siteRva))
                       .append(" fn=").append(fn == null ? "<none>"
                            : "0x" + Long.toHexString(fn.getEntryPoint().getOffset() - imageBase.getOffset())
                              + " " + fn.getName()).append("\n");
                    counts[t]++;
                }
            }
        }
        for (int t = 0; t < targets.length; t++) {
            out.append("count_+0x").append(Integer.toHexString(targets[t]))
               .append("=").append(counts[t]).append("\n");
        }
        String outPath = getEvidenceOutputPath("find-health-field-stores.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.find-health-field-stores.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println();
        w.print(out);
        w.close();
        println("VERDICT stores_b8=" + counts[0] + " stores_11e=" + counts[1]);
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
