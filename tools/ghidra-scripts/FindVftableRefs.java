// FindVftableRefs.java - byte-scan .text for references (4-byte absolute)
// to a given .rdata address — the implementing class's ctor stores the
// vftable, and callers load it to dispatch. Usage: <targetAbsRva>
import java.io.File;
import java.io.PrintWriter;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class FindVftableRefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long targetRva = 0x32daad0L;
        if (args.length > 0 && args[0].startsWith("0x")) {
            targetRva = Long.decode(args[0]);
        }
        Address imageBase = currentProgram.getImageBase();
        long targetAbs = imageBase.getOffset() + targetRva;
        StringBuilder out = new StringBuilder();
        out.append("## .text references to 0x").append(Long.toHexString(targetRva))
           .append(" (abs 0x").append(Long.toHexString(targetAbs)).append(")\n\n");
        int count = 0;
        Memory memory = currentProgram.getMemory();
        for (MemoryBlock block : memory.getBlocks()) {
            if (!block.isExecute()) continue;
            byte[] data = new byte[(int) block.getSize()];
            int read = memory.getBytes(block.getStart(), data);
            if (read != data.length) continue;
            for (int i = 0; i + 3 < read; i++) {
                long v = (data[i]&0xffL) | ((data[i+1]&0xffL)<<8)
                       | ((data[i+2]&0xffL)<<16) | ((data[i+3]&0xffL)<<24);
                if (v != targetAbs) continue;
                long siteRva = block.getStart().add(i).getOffset() - imageBase.getOffset();
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(block.getStart().add(i));
                out.append("site=0x").append(Long.toHexString(siteRva))
                   .append(" fn=").append(fn == null ? "<none>"
                        : "0x" + Long.toHexString(fn.getEntryPoint().getOffset()-imageBase.getOffset())
                        + " " + fn.getName()).append("\n");
                count++;
            }
        }
        out.append("\nref_count=").append(count).append("\n");
        String outPath = getEvidenceOutputPath("find-vftable-refs.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.print(out);
        w.close();
        println("VERDICT refs=" + count);
    }
    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"), ".build" + File.separator + "ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException("Could not create Ghidra evidence directory");
        return new File(directory, fileName).getAbsolutePath();
    }
}
