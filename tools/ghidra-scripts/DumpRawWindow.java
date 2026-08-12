// DumpRawWindow.java - dump raw dwords in a window around an RVA so vtable
// runs are identifiable by eye (which dwords are .text pointers).
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpRawWindow.java 0x3675600 80 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-l3-raw.log

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.Memory;

public class DumpRawWindow extends GhidraScript {

    private static final long IMAGE_BASE = 0x00400000L;

    @Override
    public void run() throws Exception {
        long center = 0;
        int span = 64;
        for (String a : getScriptArgs()) {
            String t = a.trim();
            if (t.startsWith("0x") || t.startsWith("0X")) {
                if (center == 0) {
                    center = Long.decode(t);
                } else {
                    span = Integer.decode(t);
                }
            }
        }
        if (center == 0) {
            println("ERROR: need RVA arg");
            return;
        }
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null) {
            outDir = "C:\\work\\wotb_reader\\.build";
        }
        File dir = new File(outDir);
        if (!dir.exists()) {
            dir.mkdirs();
        }
        PrintWriter pw = new PrintWriter(new File(dir, "raw-window.txt"), "UTF-8");
        Memory mem = currentProgram.getMemory();
        pw.println("=== raw dwords around RVA 0x" + Long.toHexString(center) +
                   " (span " + span + ") ===");
        long start = center - (long) span * 4;
        for (int i = 0; i < span * 2; i++) {
            long addr = start + i * 4L;
            long val = 0;
            try {
                val = mem.getInt(toAddr(addr)) & 0xFFFFFFFFL;
            } catch (Exception e) {
                val = 0xFFFFFFFFL;
            }
            String mark = "";
            if (val >= 0x401000 && val < 0x40200000) {
                mark = " <- .text";
            }
            String target = "";
            if (addr == IMAGE_BASE + center) {
                target = " <<<";
            }
            pw.println(String.format("  0x%08x: 0x%08x%s%s",
                    addr, val, mark, target));
        }
        pw.close();
        println("WROTE " + new File(dir, "raw-window.txt").getAbsolutePath());
    }
}
