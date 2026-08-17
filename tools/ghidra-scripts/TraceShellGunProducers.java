// TraceShellGunProducers.java - decompile the non-virtual helpers that name the
// Gun/Shell descriptor fields: direct function RVAs plus functions that
// reference the config-key string constants (maxAmmo, pumpGun*, eShellKind,
// GetShotsPerMinute, ParseBaseGunInfo, shell description strings).

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

public class TraceShellGunProducers extends GhidraScript {

    // rva, label
    private static final String[][] TARGETS = {
        { "0x440570", "ShellsReader::shell-attribute-handler" },
        { "0x31a72b4", "str:maxAmmo" },
        { "0x31a7314", "str:pumpGunMode" },
        { "0x31a7320", "str:pumpGunReloadTimes" },
        { "0x31a71ac", "str:turretRotation" },
        { "0x31a71c8", "str:whileGunDamaged" },
        { "0x31a7360", "str:Gun::GetShotsPerMinute" },
        { "0x31a725c", "str:GunsReader::ParseBaseGunInfo" },
        { "0x31a1aa8", "str:eShellKind" },
        { "0x31a1af4", "str:HOLLOW_CHARGE_DESCRIPTION" },
        { "0x31a1b44", "str:HIGH_EXPLOSIVE_DESCRIPTION" },
        { "0x31a1b94", "str:ARMOR_PIERCING_DESCRIPTION" },
        { "0x31a1bf0", "str:ARMOR_PIERCING_HE_DESCRIPTION" },
        { "0x31a1c50", "str:ARMOR_PIERCING_CR_DESCRIPTION" },
        { "0x31aa7dc", "const:_DAT_035aa7dc" },
    };

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        SymbolTable symbols = currentProgram.getSymbolTable();
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        String outPath = getEvidenceOutputPath("shell-gun-producers.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("program=" + currentProgram.getName());
        writer.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        writer.println("image_base=" + imageBase);

        for (String[] target : TARGETS) {
            long rva = Long.decode(target[0]);
            String label = target[1];
            Address address = imageBase.add(rva);

            Set<Address> functions = new LinkedHashSet<Address>();
            if (label.startsWith("str:") || label.startsWith("const:")) {
                // data target: find every referencing function
                ReferenceIterator refs = currentProgram.getReferenceManager()
                        .getReferencesTo(address);
                int refCount = 0;
                while (refs.hasNext()) {
                    Reference ref = refs.next();
                    refCount++;
                    Function fn = currentProgram.getFunctionManager()
                            .getFunctionContaining(ref.getFromAddress());
                    if (fn != null) {
                        functions.add(fn.getEntryPoint());
                    }
                }
                writer.println();
                writer.println("=== " + label + " rva=0x" + Long.toHexString(rva)
                        + " refs=" + refCount + " === ");
                // Dump the raw constant value for const targets.
                if (label.startsWith("const:")) {
                    try {
                        float f = Float.intBitsToFloat(
                                currentProgram.getMemory().getInt(address));
                        int i = currentProgram.getMemory().getInt(address);
                        writer.println("  raw_int=0x" + Integer.toHexString(i)
                                + " as_float=" + f);
                    } catch (Exception e) {
                        writer.println("  (unreadable: " + e.getMessage() + ")");
                    }
                }
            } else {
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(address);
                if (fn != null) {
                    functions.add(fn.getEntryPoint());
                }
                writer.println();
                writer.println("=== " + label + " rva=0x" + Long.toHexString(rva) + " ===");
            }

            if (functions.isEmpty()) {
                writer.println("  (no referencing function)");
                continue;
            }

            for (Address entry : functions) {
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionAt(entry);
                Symbol sym = symbols.getPrimarySymbol(entry);
                writer.println("--- fn rva=0x" + Long.toHexString(
                        entry.getOffset() - imageBase.getOffset())
                        + " " + entry
                        + " name=" + (fn == null ? "<none>" : fn.getName())
                        + " sym=" + (sym == null ? "<none>" : sym.getName(true)) + " ---");
                if (fn == null) {
                    continue;
                }
                DecompileResults results = decomp.decompileFunction(fn, 60, monitor);
                if (!results.decompileCompleted()) {
                    writer.println("  (decompile failed: " + results.getErrorMessage() + ")");
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
