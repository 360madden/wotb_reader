// DumpHierarchy.java - walk the MSVC RTTI class hierarchy from a vftable
// RVA: *(vftable-4) -> COL; COL+12 -> pTypeDesc (own name), COL+16 ->
// pHierarchy; hierarchy+8 = numBases, +12 = base array of 8-byte
// _RTTIBaseClassDescriptor pointers; each base descriptor (x86) has
// pTypeDescriptor at +0x00 and pVTable at +0x0C. Prints every base's type
// name + vftable RVA.
//
// Usage: -postScript DumpHierarchy.java <vftableRva>
//
// Hash-bound static evidence only; no live read, no promotion.
import java.io.File;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

public class DumpHierarchy extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long vftableRva = 0x326dd0cL;
        if (args.length > 0 && args[0].startsWith("0x")) {
            vftableRva = Long.decode(args[0]);
        }
        Address imageBase = currentProgram.getImageBase();
        long base = imageBase.getOffset();
        long vftableAbs = base + vftableRva;

        StringBuilder out = new StringBuilder();
        out.append("## RTTI hierarchy for vftable 0x")
           .append(Long.toHexString(vftableRva)).append("\n\n");

        long colAbs = readU32(vftableAbs - 4);
        out.append("COL abs=0x").append(Long.toHexString(colAbs)).append("\n");
        if (colAbs == -1L || colAbs == 0) {
            out.append("ERROR: no COL pointer\n");
            writeReport(out.toString(), "hierarchy-no-col");
            return;
        }
        long hierAbs = readU32(colAbs + 16);
        long ownTypeDescAbs = readU32(colAbs + 12);
        out.append("own_type=0x").append(Long.toHexString(ownTypeDescAbs))
           .append(" ").append(readCString(ownTypeDescAbs + 8, 256)).append("\n");
        out.append("hierarchy abs=0x").append(Long.toHexString(hierAbs)).append("\n");
        if (hierAbs == 0 || hierAbs == -1L) {
            out.append("ERROR: no hierarchy\n");
            writeReport(out.toString(), "hierarchy-none");
            return;
        }
        int numBases = (int) readU32(hierAbs + 8);
        long baseArrayAbs = readU32(hierAbs + 12);
        out.append("num_bases=").append(numBases)
           .append(" base_array=0x").append(Long.toHexString(baseArrayAbs)).append("\n");
        for (int i = 0; i < numBases; i++) {
            long descAbs = readU32(baseArrayAbs + i * 4L);
            long tdAbs = readU32(descAbs + 0x00);
            // PMD (mdisp/pdisp/vdisp) is 12 bytes at +0x04..+0x10.
            long pVTable = readU32(descAbs + 0x14);
            String name = readCString(tdAbs + 8, 256);
            out.append("base[").append(i).append("] desc=0x")
               .append(Long.toHexString(descAbs))
               .append(" name=").append(name)
               .append(" name_str_abs=0x").append(Long.toHexString(tdAbs + 8))
               .append(" vftable_abs=0x").append(Long.toHexString(pVTable))
               .append(" rva=0x").append(Long.toHexString(pVTable - base))
               .append("\n");
        }

        writeReport(out.toString(), "hierarchy-" + Long.toHexString(vftableRva));
        println("HIERARCHY bases=" + numBases);
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

    private void writeReport(String content, String name) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build" + File.separator + "ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException(
                    "Could not create Ghidra evidence directory");
        PrintWriter w = new PrintWriter(
                new File(directory, name + ".txt"), StandardCharsets.UTF_8);
        w.print(content);
        w.close();
    }
}
