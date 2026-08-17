// TraceGunEquipWriters.java - locate the VehicleGun "equip/configure" path.
//
// The ctor (FUN_01da8bb0) and allocating factory (FUN_01d9cc30) hardcode
// +0x38=100.0f / +0x3c=9 / +0x40=1.0f as class defaults, so the per-instance
// configured-gun / loaded-shell values must be written later. This script
// enumerates the "gun-aware" function set — every function that references
// the VehicleGun vtable (data refs plus MOV immediates), the known gun
// creators, and their callers — and reports each instruction that reads or
// writes the gun field block (0x0..0x64), with decompiled context, so the
// descriptor producer can be followed.
//
// Usage (project already analyzed; use -noanalysis):
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript TraceGunEquipWriters.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-gun-equip.log

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;
import java.util.TreeSet;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class TraceGunEquipWriters extends GhidraScript {

    private static final long VTABLE_RVA = 0x32dacf4L; // VehicleGun primary vftable
    private static final long BASE_DELTA = 0x400000L;
    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;
    private static final int GUN_SIZE = 0x64; // 100-byte VehicleGun object

    private static final long[] SEEDS = {
        0x19a8bb0L, // in-place ctor
        0x199cc30L, // allocating factory
        0x19b0ff0L, // dtor
        0x1283b00L, // AvatarGameLogic ctor (stores gun at +0x204)
        0x19a3f20L  // GunStatusPresenter ctor (owns a gun at +0x4)
    };

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        String outPath = getEvidenceOutputPath("gun-equip-writers.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.gun-equip-writers.v2");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        w.println("image_base=" + imageBase);
        w.println("vtable_rva=0x" + Long.toHexString(VTABLE_RVA));
        w.println("gun_size=0x" + Integer.toHexString(GUN_SIZE));
        w.println();

        Listing listing = currentProgram.getListing();
        Address vtable = imageBase.add(VTABLE_RVA);

        Set<Long> aware = new LinkedHashSet<Long>();
        for (long rva : SEEDS) {
            aware.add(rva);
        }

        // 1. References to the vtable address.
        ReferenceIterator refs = currentProgram.getReferenceManager()
                .getReferencesTo(vtable);
        while (refs.hasNext()) {
            Reference ref = refs.next();
            Function f = currentProgram.getFunctionManager()
                    .getFunctionContaining(ref.getFromAddress());
            if (f != null) {
                aware.add(f.getEntryPoint().getOffset() - imageBase.getOffset());
            }
        }

        // 2. MOV immediates that install the vtable (RVA or RVA + base).
        long[] imms = { VTABLE_RVA, VTABLE_RVA + BASE_DELTA };
        AddressSet range = new AddressSet(imageBase.add(CODE_LO),
                imageBase.add(CODE_HI));
        for (Instruction insn : listing.getInstructions(range, true)) {
            if (!"MOV".equals(insn.getMnemonicString())
                    || insn.getNumOperands() < 2) {
                continue;
            }
            Object[] ops = insn.getOpObjects(1);
            if (ops == null || ops.length == 0 || !(ops[0] instanceof Scalar)) {
                continue;
            }
            long v = ((Scalar) ops[0]).getUnsignedValue();
            for (long imm : imms) {
                if (v == imm) {
                    Function f = currentProgram.getFunctionManager()
                            .getFunctionContaining(insn.getAddress());
                    if (f != null) {
                        aware.add(f.getEntryPoint().getOffset()
                                - imageBase.getOffset());
                    }
                    break;
                }
            }
        }

        // 3. Callers of each seed (one hop up).
        for (long rva : SEEDS) {
            Address entry = imageBase.add(rva);
            Function seed = currentProgram.getFunctionManager()
                    .getFunctionAt(entry);
            if (seed == null) {
                seed = currentProgram.getFunctionManager()
                        .getFunctionContaining(entry);
            }
            if (seed == null) {
                continue;
            }
            ReferenceIterator callers = currentProgram.getReferenceManager()
                    .getReferencesTo(seed.getEntryPoint());
            while (callers.hasNext()) {
                Reference ref = callers.next();
                if (!ref.getReferenceType().isCall()) {
                    continue;
                }
                Function caller = currentProgram.getFunctionManager()
                        .getFunctionContaining(ref.getFromAddress());
                if (caller != null) {
                    aware.add(caller.getEntryPoint().getOffset()
                            - imageBase.getOffset());
                }
            }
        }

        w.println("gun-aware function count=" + aware.size());

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        for (long rva : aware) {
            Address entry = imageBase.add(rva);
            Function func = currentProgram.getFunctionManager()
                    .getFunctionAt(entry);
            if (func == null) {
                func = currentProgram.getFunctionManager()
                        .getFunctionContaining(entry);
            }
            if (func == null) {
                continue;
            }

            TreeSet<String> writes = new TreeSet<String>();
            TreeSet<String> reads = new TreeSet<String>();
            Instruction insn = listing.getInstructionAt(func.getEntryPoint());
            while (insn != null && func.getBody().contains(insn.getAddress())) {
                String text = insn.toString();
                String hit = fieldBlockHit(text);
                if (hit != null) {
                    String line = "  " + insn.getAddress() + ": " + text;
                    if (isWrite(insn, text)) {
                        writes.add(line);
                    } else {
                        reads.add(line);
                    }
                }
                insn = insn.getNext();
            }

            if (writes.isEmpty() && reads.isEmpty()) {
                continue;
            }

            w.println();
            w.println("### function rva=0x" + Long.toHexString(rva)
                    + " name=" + func.getName());
            if (!writes.isEmpty()) {
                w.println("--- gun field-block writes ---");
                for (String s : writes) {
                    w.println(s);
                }
            }
            if (!reads.isEmpty()) {
                w.println("--- gun field-block reads ---");
                for (String s : reads) {
                    w.println(s);
                }
            }
            DecompileResults results = decomp.decompileFunction(func, 60, monitor);
            if (results.decompileCompleted()) {
                w.println("--- decompiled (C) ---");
                w.println(results.getDecompiledFunction().getC());
            } else {
                w.println("(decompile failed: " + results.getErrorMessage() + ")");
            }
            w.println("--- callers ---");
            ReferenceIterator callers = currentProgram.getReferenceManager()
                    .getReferencesTo(func.getEntryPoint());
            int shown = 0;
            while (callers.hasNext() && shown < 20) {
                Reference ref = callers.next();
                if (!ref.getReferenceType().isCall()) {
                    continue;
                }
                Address from = ref.getFromAddress();
                Function caller = currentProgram.getFunctionManager()
                        .getFunctionContaining(from);
                long fromRva = from.getOffset() - imageBase.getOffset();
                w.println("  CALL from_rva=0x" + Long.toHexString(fromRva)
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

    // Positive-offset field access in the gun block, e.g. "[EDI + 0x3c]".
    // Negative stack offsets ("[EBP + -0x3c]") are excluded by requiring the
    // "+ 0x" pattern and a non-ESP/EBP base is left to review.
    private String fieldBlockHit(String text) {
        String lower = text.toLowerCase();
        for (int off = 0x10; off < GUN_SIZE; off += 4) {
            String hex = "0x" + Integer.toHexString(off);
            if (lower.contains("+ " + hex + "]")) {
                return hex;
            }
        }
        return null;
    }

    private boolean isWrite(Instruction insn, String text) {
        String mnem = insn.getMnemonicString();
        if (mnem.startsWith("MOV") || mnem.equals("FLD")
                || mnem.equals("FSTP") || mnem.equals("MOVD")
                || mnem.equals("MOVSD") || mnem.equals("MOVSS")
                || mnem.equals("MOVUPS") || mnem.equals("MOVAPS")) {
            int comma = text.indexOf(',');
            int bracket = text.indexOf('[');
            return bracket >= 0 && (comma < 0 || bracket < comma);
        }
        return true; // LEA / etc: treat as write candidate for review
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
