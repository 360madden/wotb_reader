// DumpVtableSlots.java - list bounded function-pointer slots for one or more
// vtable RVAs. Reports stay under the ignored Ghidra evidence directory.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;

public class DumpVtableSlots extends GhidraScript {

    private static final int SLOT_COUNT = 8;

    @Override
    public void run() throws Exception {
        List<Long> rvas = new ArrayList<Long>();
        for (String argument : getScriptArgs()) {
            String value = argument.trim();
            if (value.startsWith("0x") || value.startsWith("0X")) {
                rvas.add(Long.decode(value));
            }
        }
        if (rvas.isEmpty()) {
            println("ERROR: no vtable RVA argument provided");
            return;
        }

        Address imageBase = currentProgram.getImageBase();
        String outPath = getEvidenceOutputPath("vtable-slots.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("program=" + currentProgram.getName());
        writer.println("image_base=" + imageBase);
        writer.println("slot_count=" + SLOT_COUNT);

        for (long rva : rvas) {
            Address table = imageBase.add(rva);
            Symbol tableSymbol = currentProgram.getSymbolTable()
                    .getPrimarySymbol(table);
            writer.println();
            writer.println("vtable_rva=0x" + Long.toHexString(rva));
            writer.println("vtable=" + table);
            writer.println("vtable_symbol=" + (tableSymbol == null
                    ? "<none>"
                    : tableSymbol.getName(true)));

            for (int slot = 0; slot < SLOT_COUNT; slot++) {
                Address slotAddress = table.add((long)slot * 4L);
                long targetOffset = Integer.toUnsignedLong(
                        currentProgram.getMemory().getInt(slotAddress));
                Address target = currentProgram.getAddressFactory()
                        .getDefaultAddressSpace().getAddress(targetOffset);
                long targetRva = targetOffset - imageBase.getOffset();
                Function function = currentProgram.getFunctionManager()
                        .getFunctionAt(target);
                Symbol symbol = currentProgram.getSymbolTable()
                        .getPrimarySymbol(target);
                writer.println("slot=" + slot +
                        " target_rva=0x" + Long.toHexString(targetRva) +
                        " target=" + target +
                        " function=" + (function == null
                                ? "<none>"
                                : function.getName()) +
                        " symbol=" + (symbol == null
                                ? "<none>"
                                : symbol.getName(true)));
            }
        }

        writer.close();
        println("WROTE " + outPath);
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
