// ListGunSymbols.java - list symbols whose demangled name matches gun/shell/
// ammo/descriptor keywords, so the gun-descriptor type and its accessors can
// be identified for the configured-gun/loaded-shell producer trace.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript ListGunSymbols.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-gun-symbols.log

import java.io.File;
import java.io.PrintWriter;
import java.util.TreeSet;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolTable;

public class ListGunSymbols extends GhidraScript {

    private static final String[] KEYS = {
        "gun", "shell", "ammo", "turret", "descr", "weapon"
    };

    @Override
    public void run() throws Exception {
        String outPath = getEvidenceOutputPath("gun-symbols.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.gun-symbols.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("image_base=" + currentProgram.getImageBase());
        w.println();

        SymbolTable symbols = currentProgram.getSymbolTable();
        SymbolIterator it = symbols.getAllSymbols(true);
        TreeSet<String> out = new TreeSet<String>();
        long imageBase = currentProgram.getImageBase().getOffset();
        while (it.hasNext()) {
            Symbol sym = it.next();
            String name = sym.getName(true);
            String lower = name.toLowerCase();
            for (String key : KEYS) {
                if (lower.contains(key)) {
                    out.add("rva=0x" + Long.toHexString(
                            sym.getAddress().getOffset() - imageBase)
                            + "  " + sym.getAddress() + "  " + name);
                    break;
                }
            }
        }
        w.println("matched_symbols=" + out.size());
        for (String line : out) {
            w.println(line);
        }
        w.close();
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
