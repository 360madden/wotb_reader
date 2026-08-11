// ScanRttiHealthClasses.java - enumerate RTTI TypeDescriptor names that
// look like health/damage/vehicle state classes (playerHP static work).
// Walks every RTTI Complete Object Locator's TypeDescriptor, reads the
// name string (RTTI type names live in .rdata), and reports names matching
// a health/damage/hp/vehicle/damage-model pattern with their RVA.
//
// This is triage evidence for the HP discovery target: a named component
// class whose vtable/fields could hold the HP int32. Hash-bound static
// evidence only; no live read, no promotion.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;

public class ScanRttiHealthClasses extends GhidraScript {

    private final List<String> hits = new ArrayList<String>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();

        out.append("## RTTI type-name scan (health/damage/vehicle patterns)\n\n");

        // MSVC RTTI TypeDescriptor names are exposed as Ghidra symbols with
        // names like "BW::Vehicle::RTTI_Type_Descriptor" or
        // ".?AVClassName@@". Walk every symbol; keep ones whose name or
        // demangled form matches the keyword set.
        String[] keywords = {
            "Health", "health", "HP", "Hp", "Damage", "damage",
            "HitPoint", "Hitpoint", "hitpoint", "Vehicle", "vehicle",
            "Tank", "tank", "Arena", "arena", "Avatar", "avatar",
            "Entity", "entity", "Life", "life", "Armor", "armor",
            "health", "HealthComponent", "VehicleHealth"
        };

        int total = 0;
        SymbolIterator symbols = currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext() && monitor.isCancelled() == false) {
            Symbol symbol = symbols.next();
            String name = symbol.getName(true);
            String simple = symbol.getName();
            boolean isRtti = name.contains("RTTI_Type_Descriptor")
                    || name.contains(".?AV") || name.contains("@")
                    || name.contains("RTTI");
            if (!isRtti) {
                continue;
            }
            boolean matched = false;
            for (String kw : keywords) {
                if (name.contains(kw)) {
                    matched = true;
                    break;
                }
            }
            if (!matched) {
                continue;
            }
            long rva = symbol.getAddress().getOffset()
                    - imageBase.getOffset();
            hits.add("rva=0x" + Long.toHexString(rva) + " " + name);
            total++;
        }
        // Dedupe by name, keep first RVA.
        java.util.LinkedHashMap<String, String> dedup =
                new java.util.LinkedHashMap<String, String>();
        for (String hit : hits) {
            int sp = hit.indexOf(' ');
            String rva = hit.substring(0, sp);
            String name = hit.substring(sp + 1);
            dedup.putIfAbsent(name, rva);
        }
        out.append("total_raw_hits=").append(total)
                .append(" unique_names=").append(dedup.size()).append("\n\n");
        for (java.util.Map.Entry<String, String> e : dedup.entrySet()) {
            out.append(e.getValue()).append("  ").append(e.getKey()).append("\n");
        }

        writeReport(out.toString(), total, dedup.size());
        println("WROTE scan-rtti-health-classes.txt unique=" + dedup.size());
    }

    private void writeReport(String body, int total, int unique)
            throws Exception {
        String outPath = getEvidenceOutputPath("scan-rtti-health-classes.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.scan-rtti-health-classes.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("raw_hits=" + total);
        w.println("unique_names=" + unique);
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
