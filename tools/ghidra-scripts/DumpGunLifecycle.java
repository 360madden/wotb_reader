// DumpGunLifecycle.java - decompile the VehicleGun creation/configuration
// lifecycle (the AvatarGameLogic ctor that allocates the gun, the gun factory,
// and VehicleGameLogic::onEnterWorld) and report every instruction that writes
// or reads the gun object's field block, so the configured-gun / loaded-shell
// path can be separated from the hardcoded ctor defaults.
//
// The gun object is stored at AvatarGameLogic +0x204 (proven by the ownership
// walk). The ctor hardcodes +0x38=100.0f / +0x3c=9 / +0x40=1.0f, so the
// per-instance configuration must be written afterwards; this dumps the
// callers of that ctor to find it.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript DumpGunLifecycle.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-gun-lifecycle.log

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class DumpGunLifecycle extends GhidraScript {

    // Gun-field block of interest (the ctor writes these): +0x38..+0x60.
    private static final int[] FIELDS = {
        0x38, 0x3c, 0x40, 0x44, 0x48, 0x4c, 0x50, 0x54, 0x58, 0x5c, 0x60
    };

    @Override
    public void run() throws Exception {
        // Lifecycle functions (RVAs). FUN_xxxx names are absolute; subtract
        // 0x400000 for the RVA.
        long[] rvas = {
            0x1283b00L, // AvatarGameLogic ctor (allocates gun at +0x204)
            0x19a3f20L, // caller of the VehicleGun allocating factory
            0x199cc30L, // VehicleGun allocating factory
            0x12ea010L  // VehicleGameLogic::onEnterWorld (rotator +0x1fc)
        };

        Address imageBase = currentProgram.getImageBase();
        String outPath = getEvidenceOutputPath("gun-lifecycle.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.gun-lifecycle.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("image_base=" + imageBase);
        w.println("fields=0x38..0x60 (gun field block)");
        w.println();

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        Listing listing = currentProgram.getListing();

        for (long rva : rvas) {
            Address entry = imageBase.add(rva);
            Function func = currentProgram.getFunctionManager()
                    .getFunctionAt(entry);
            if (func == null) {
                func = currentProgram.getFunctionManager()
                        .getFunctionContaining(entry);
            }
            w.println();
            w.println("### function rva=0x" + Long.toHexString(rva)
                    + " name=" + (func == null ? "<none>" : func.getName()));
            if (func == null) {
                w.println("(no function at this address; skipped)");
                continue;
            }
            w.println("entry=" + func.getEntryPoint() + " body="
                    + func.getBody().getMinAddress() + ".."
                    + func.getBody().getMaxAddress());

            w.println("--- field-access disassembly (matched) ---");
            Instruction insn = listing.getInstructionAt(func.getEntryPoint());
            int matched = 0;
            while (insn != null && func.getBody().contains(insn.getAddress())) {
                String text = insn.toString();
                if (hitsField(text)) {
                    w.println("  " + insn.getAddress() + ": " + text);
                    matched++;
                }
                insn = insn.getNext();
            }
            w.println("matched_instructions=" + matched);

            DecompileResults results = decomp.decompileFunction(func, 60, monitor);
            if (results.decompileCompleted()) {
                w.println("--- decompiled (C) ---");
                w.println(results.getDecompiledFunction().getC());
            } else {
                w.println("(decompile failed: " + results.getErrorMessage() + ")");
            }

            w.println("--- callers ---");
            ReferenceIterator refs = currentProgram.getReferenceManager()
                    .getReferencesTo(func.getEntryPoint());
            int shown = 0;
            while (refs.hasNext() && shown < 20) {
                Reference ref = refs.next();
                if (!ref.getReferenceType().isCall()) {
                    continue;
                }
                Address from = ref.getFromAddress();
                Function caller = currentProgram.getFunctionManager()
                        .getFunctionContaining(from);
                long fromRva = from.getOffset() - imageBase.getOffset();
                w.println("  CALL from_rva=0x" + Long.toHexString(fromRva)
                        + " from=" + from
                        + " fn=" + (caller == null ? "<none>" : caller.getName()));
                shown++;
            }
            if (shown == 0) {
                w.println("  (no call callers)");
            }
        }

        decomp.dispose();
        w.close();
        println("WROTE " + outPath);
    }

    private boolean hitsField(String text) {
        String lower = text.toLowerCase();
        for (int f : FIELDS) {
            String hex = "0x" + Integer.toHexString(f);
            if (lower.contains(hex + "]")) {
                return true;
            }
        }
        return false;
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
