// ResolveVftableClass.java - resolve the MSVC RTTI class name behind a
// vftable RVA. MSVC layout (x86): *(vftable-4) -> COL; COL+12 -> pTypeDesc;
// TypeDesc+8 -> mangled name. Also reports the base-class chain via the
// class hierarchy descriptor.
// Usage: -postScript ResolveVftableClass.java <vftableRva>
import java.io.File;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

public class ResolveVftableClass extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long vftableRva = 0x36daaecL;
        if (args.length > 0 && args[0].startsWith("0x")) {
            vftableRva = Long.decode(args[0]);
        }
        Address imageBase = currentProgram.getImageBase();
        Memory mem = currentProgram.getMemory();
        StringBuilder out = new StringBuilder();
        out.append("## RTTI resolution for vftable 0x")
           .append(Long.toHexString(vftableRva)).append("\n\n");

        long vftableAbs = imageBase.getOffset() + vftableRva;
        long colAbs = readU32(vftableAbs - 4);
        out.append("COL abs=0x").append(Long.toHexString(colAbs)).append("\n");
        if (colAbs == -1L || colAbs == 0) {
            out.append("ERROR: no COL pointer\n");
            writeReport(out.toString(), "no-col");
            return;
        }
        long typeDescAbs = readU32(colAbs + 12);
        long hierDescAbs = readU32(colAbs + 16);
        String name = readCString(typeDescAbs + 8, 256);
        out.append("type_descriptor abs=0x").append(Long.toHexString(typeDescAbs))
           .append(" name=").append(name).append("\n");

        if (hierDescAbs != 0 && hierDescAbs != -1L) {
            int numBases = (int) readU32(hierDescAbs + 8);
            long baseArrAbs = readU32(hierDescAbs + 12);
            out.append("hierarchy num_bases=").append(numBases)
               .append(" base_array=0x").append(Long.toHexString(baseArrAbs)).append("\n");
            for (int i = 0; i < numBases && i < 24; i++) {
                long bcdAbs = readU32(baseArrAbs + i * 4L);
                long btAbs = readU32(bcdAbs);
                String bn = readCString(btAbs + 8, 256);
                out.append("  base[").append(i).append("]=").append(bn).append("\n");
            }
        }
        writeReport(out.toString(), "resolved:" + name);
        println("VERDICT class=" + name);
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
            if (read <= 0) return "<unreadable>";
            int len = 0;
            while (len < read && b[len] != 0) len++;
            return new String(b, 0, len, StandardCharsets.UTF_8);
        } catch (Exception e) {
            return "<error>";
        }
    }

    private void writeReport(String body, String verdict) throws Exception {
        String outPath = getEvidenceOutputPath("resolve-vftable-class.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.resolve-vftable-class.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("verdict=" + verdict);
        w.println();
        w.print(body);
        w.close();
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
