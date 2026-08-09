// FindFunctionReferences.java - list every code and data reference to one or
// more function RVAs, including the nearest preceding named data symbol.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

public class FindFunctionReferences extends GhidraScript {

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
            println("ERROR: no RVA argument provided");
            return;
        }

        Address imageBase = currentProgram.getImageBase();
        SymbolTable symbols = currentProgram.getSymbolTable();
        String outPath = getEvidenceOutputPath("function-references.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("program=" + currentProgram.getName());
        writer.println("image_base=" + imageBase);

        for (long rva : rvas) {
            Address target = imageBase.add(rva);
            Function targetFunction = currentProgram.getFunctionManager()
                    .getFunctionContaining(target);
            writer.println();
            writer.println("target_rva=0x" + Long.toHexString(rva));
            writer.println("target=" + target);
            Symbol targetSymbol = symbols.getPrimarySymbol(target);
            writer.println("target_symbol=" + (targetSymbol == null
                    ? "<none>"
                    : targetSymbol.getName(true)));
            writer.println("function=" + (targetFunction == null
                    ? "<none>"
                    : targetFunction.getName()));

            ReferenceIterator references = currentProgram.getReferenceManager()
                    .getReferencesTo(targetFunction == null
                            ? target
                            : targetFunction.getEntryPoint());
            int count = 0;
            while (references.hasNext()) {
                Reference reference = references.next();
                Address from = reference.getFromAddress();
                Function fromFunction = currentProgram.getFunctionManager()
                        .getFunctionContaining(from);
                Symbol direct = symbols.getPrimarySymbol(from);
                Symbol preceding = direct;
                if (preceding == null) {
                    ghidra.program.model.symbol.SymbolIterator prior =
                            symbols.getSymbolIterator(from, false);
                    if (prior.hasNext()) {
                        preceding = prior.next();
                    }
                }
                writer.println("ref=" + reference.getReferenceType() +
                        " from=" + from +
                        " from_rva=0x" + Long.toHexString(
                                from.getOffset() - imageBase.getOffset()) +
                        " function=" + (fromFunction == null
                                ? "<none>"
                                : fromFunction.getName()) +
                        " symbol=" + (preceding == null
                                ? "<none>"
                                : preceding.getName(true)) +
                        " symbol_address=" + (preceding == null
                                ? "<none>"
                                : preceding.getAddress()));
                count++;
            }
            writer.println("reference_count=" + count);
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
