// DumpDescriptorVtables.java - enumerate and decompile the vtable methods of
// descriptor classes (Gun/Shell/VehicleDescr and their *Reader parsers) so the
// this-relative field offsets can be read out of the accessor/parser bodies.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpDescriptorVtables.java 0x31a7080 0x31a1e14 0x31a703c 0x31aa0b8 \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-gun-layout.log

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

public class DumpDescriptorVtables extends GhidraScript {

    private static final int MAX_SLOTS = 64;

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
        SymbolTable symbols = currentProgram.getSymbolTable();
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        String outPath = getEvidenceOutputPath("descriptor-vtables.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("program=" + currentProgram.getName());
        writer.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        writer.println("image_base=" + imageBase);

        for (long rva : rvas) {
            Address table = imageBase.add(rva);
            Symbol tableSymbol = symbols.getPrimarySymbol(table);
            writer.println();
            writer.println("=== vtable rva=0x" + Long.toHexString(rva)
                    + " symbol=" + (tableSymbol == null
                            ? "<none>"
                            : tableSymbol.getName(true)) + " ===");

            for (int slot = 0; slot < MAX_SLOTS; slot++) {
                Address slotAddress = table.add((long) slot * 4L);
                if (!currentProgram.getMemory().contains(slotAddress)) {
                    break;
                }

                long targetOffset = Integer.toUnsignedLong(
                        currentProgram.getMemory().getInt(slotAddress));
                if (targetOffset == 0) {
                    break;
                }

                Address target = currentProgram.getAddressFactory()
                        .getDefaultAddressSpace().getAddress(targetOffset);
                MemoryBlock block = currentProgram.getMemory().getBlock(target);
                if (block == null || !block.isExecute()) {
                    break;
                }

                Function function = currentProgram.getFunctionManager()
                        .getFunctionContaining(target);
                Symbol symbol = symbols.getPrimarySymbol(target);
                writer.println();
                writer.println("--- slot=" + slot
                        + " target_rva=0x" + Long.toHexString(
                                targetOffset - imageBase.getOffset())
                        + " target=" + target
                        + " fn=" + (function == null
                                ? "<none>"
                                : function.getName())
                        + " sym=" + (symbol == null
                                ? "<none>"
                                : symbol.getName(true)) + " ---");

                if (function == null) {
                    continue;
                }

                DecompileResults results =
                        decomp.decompileFunction(function, 60, monitor);
                if (!results.decompileCompleted()) {
                    writer.println("(decompile failed: "
                            + results.getErrorMessage() + ")");
                    continue;
                }

                writer.println(results.getDecompiledFunction().getC());
            }
        }

        decomp.dispose();
        writer.close();
        println("WROTE " + outPath);
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build\\ghidra-evidence-gun-fields")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs()) {
            throw new IllegalStateException(
                    "Could not create Ghidra evidence directory");
        }
        return new File(directory, fileName).getAbsolutePath();
    }
}
