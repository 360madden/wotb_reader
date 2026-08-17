// TraceGunFieldAccess.java - static write/read-site trace for the VehicleGun
// configured-gun / loaded-shell candidate fields.
//
// The ownership walk already pinned the object (VehicleGun at AvatarGameLogic
// +0x204) and the DumpRange pass mapped the ctor writes to concrete offsets:
//   +0x38 = 100.0f   +0x3c = 9 (int)   +0x40 = 1.0f
// This script enumerates every VehicleGun virtual method (vtable 0x32dacf4)
// plus the known ctor / allocating factory / dtor, and reports every
// instruction that touches those offsets, with decompiled context, so the
// field can be classified by data flow instead of guessed.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript TraceGunFieldAccess.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-gun-fields.log

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
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class TraceGunFieldAccess extends GhidraScript {

    private static final long VTABLE_RVA = 0x32dacf4L; // VehicleGun primary vftable
    private static final int[] FIELDS = { 0x38, 0x3c, 0x40 };
    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;
    private static final int MAX_SLOTS = 64;

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        String outPath = getEvidenceOutputPath("gun-field-trace.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.gun-field-trace.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("image_base=" + imageBase);
        w.println("vtable_rva=0x" + Long.toHexString(VTABLE_RVA));
        w.println("fields=0x38,0x3c,0x40");
        w.println();

        // 1. Enumerate vtable slots.
        Memory mem = currentProgram.getMemory();
        Address table = imageBase.add(VTABLE_RVA);
        Set<Long> rvas = new LinkedHashSet<Long>();
        w.println("## VehicleGun vtable slots");
        for (int slot = 0; slot < MAX_SLOTS; slot++) {
            Address slotAddr = table.add((long) slot * 4L);
            long val = 0;
            try {
                val = mem.getInt(slotAddr) & 0xFFFFFFFFL;
            } catch (Exception e) {
                break;
            }
            if (val < CODE_LO || val >= CODE_HI) {
                w.println("slot=" + slot + " <end/invalid 0x"
                        + Long.toHexString(val) + ">");
                break;
            }
            long targetRva = val - imageBase.getOffset();
            w.println("slot=" + slot + " target_rva=0x"
                    + Long.toHexString(targetRva));
            rvas.add(targetRva);
        }
        w.println();

        // Known non-virtual members.
        long[] extra = { 0x19a8bb0L, // in-place ctor
                         0x19a9cc30L, // allocating factory
                         0x19b0ff0L }; // dtor (== slot 0)
        for (long rva : extra) {
            rvas.add(rva);
        }

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        Listing listing = currentProgram.getListing();

        // 2. Per function: matched disassembly + decompile + callers.
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

            w.println("--- field-access disassembly (matched) ---");
            int matched = 0;
            Instruction insn = listing.getInstructionAt(func.getEntryPoint());
            while (insn != null
                    && func.getBody().contains(insn.getAddress())) {
                String text = insn.toString();
                if (hitsField(text)) {
                    w.println("  " + insn.getAddress() + ": " + text);
                    matched++;
                }
                insn = insn.getNext();
            }
            w.println("matched_instructions=" + matched);

            DecompileResults results = decomp.decompileFunction(func, 30, monitor);
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
            // Match exactly the bracketed access, e.g. "[ECX + 0x38]".
            // Anchoring on the closing ']' avoids 0x138 / 0x40 vs 0x140 noise.
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
