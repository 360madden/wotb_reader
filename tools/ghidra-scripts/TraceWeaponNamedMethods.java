// TraceWeaponNamedMethods.java - dump VehicleGun / VehicleGunRotator
// methods identified by source assert strings, plus secondary vtable
// slices. The primary vftables are dtor+RTTI only; domain methods are
// named non-virtuals.
//
// Hash-bound static evidence only. Does not promote offsets.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class TraceWeaponNamedMethods extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";
    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;
    private static final int MAX_DECOMPILE = 18;

    private static final String[] NEEDLES = {
        "VehicleGun::",
        "VehicleGunRotator::",
        "updateVehicleGun",
        "GetGunMarkerPosition",
        "GetTurretAngle"
    };

    private static final long[] EXTRA_RVAS = {
        0x1ac50e0L, // VehicleGunRotator primary slot 1
        0x1ac5760L, // VehicleGunRotator primary slot 2
        0x50acb0L   // VehicleGun primary slot 2 (already seen; skip if named)
    };

    private static final long[] VTABLE_WINDOWS = {
        0x32dacf4L, // VehicleGun primary
        0x32eeb40L, // VehicleGunRotator primary
        0x32eeb54L, // suspected FollowAimListener slice
        0x32eeb64L, // suspected DevOptionsDelegate slice
        0x324dae8L  // AvatarGunAgent
    };

    private PrintWriter pw;
    private DecompInterface decomp;
    private long imageBase;

    @Override
    public void run() throws Exception {
        String actual = currentProgram.getExecutableSHA256();
        if (actual == null || !EXPECTED_SHA256.equalsIgnoreCase(actual)) {
            throw new IllegalStateException(
                    "hash mismatch expected=" + EXPECTED_SHA256 + " actual=" + actual);
        }
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null || outDir.trim().isEmpty()) {
            outDir = new File(System.getProperty("user.dir"),
                    ".build\\ghidra-evidence-weapon-fields").getAbsolutePath();
        }
        File dir = new File(outDir);
        if (!dir.isDirectory() && !dir.mkdirs()) {
            throw new IllegalStateException("Could not create " + dir);
        }
        pw = new PrintWriter(new File(dir, "weapon-named-methods.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.trace-weapon-named-methods.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + actual);
        pw.println("hash_match=true");
        pw.println();

        imageBase = currentProgram.getImageBase().getOffset();
        decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        dumpVtableWindows();
        Set<Long> rvas = collectNamedMethods();
        for (long extra : EXTRA_RVAS) {
            rvas.add(extra);
        }

        pw.println();
        pw.println("============================================================");
        pw.println("## decompiled named / extra methods");
        pw.println("============================================================");
        int n = 0;
        for (Long rva : rvas) {
            if (n >= MAX_DECOMPILE) {
                pw.println("(decompile cap reached)");
                break;
            }
            if (dumpFunction(rva)) {
                n++;
            }
        }

        decomp.dispose();
        pw.close();
        println("WROTE " + new File(dir, "weapon-named-methods.txt").getAbsolutePath()
                + " methods=" + n);
    }

    private void dumpVtableWindows() throws Exception {
        Memory mem = currentProgram.getMemory();
        pw.println("============================================================");
        pw.println("## vtable dword windows");
        pw.println("============================================================");
        for (long rva : VTABLE_WINDOWS) {
            pw.println();
            pw.println("### window rva=0x" + Long.toHexString(rva));
            Address base = toAbs(rva);
            for (int i = 0; i < 16; i++) {
                Address slot = base.add((long) i * 4L);
                long value = Integer.toUnsignedLong(mem.getInt(slot));
                String kind = "data";
                String extra = "";
                if (value >= CODE_LO && value <= CODE_HI) {
                    Address target = currentProgram.getAddressFactory()
                            .getDefaultAddressSpace().getAddress(value);
                    Function fn = currentProgram.getFunctionManager().getFunctionAt(target);
                    kind = fn == null ? "code-nofn" : "code";
                    extra = " rva=0x" + Long.toHexString(value - imageBase)
                            + (fn == null ? "" : " fn=" + fn.getName());
                } else {
                    extra = " abs=0x" + Long.toHexString(value);
                }
                pw.println("    +" + Integer.toHexString(i * 4) + " " + kind + extra);
            }
        }
    }

    private Set<Long> collectNamedMethods() {
        pw.println();
        pw.println("============================================================");
        pw.println("## named method strings");
        pw.println("============================================================");
        Listing listing = currentProgram.getListing();
        Map<Long, String> found = new LinkedHashMap<Long, String>();
        for (Data data : listing.getDefinedData(true)) {
            if (!data.hasStringValue()) {
                continue;
            }
            String value = data.getDefaultValueRepresentation();
            if (value == null) {
                continue;
            }
            boolean match = false;
            for (String needle : NEEDLES) {
                if (value.indexOf(needle) >= 0) {
                    match = true;
                    break;
                }
            }
            if (!match) {
                continue;
            }
            pw.println();
            pw.println("str@0x" + Long.toHexString(data.getAddress().getOffset() - imageBase)
                    + " " + trim(value, 100));
            ReferenceIterator refs = currentProgram.getReferenceManager()
                    .getReferencesTo(data.getAddress());
            int shown = 0;
            while (refs.hasNext() && shown < 16) {
                Reference r = refs.next();
                Address from = r.getFromAddress();
                Function fn = currentProgram.getFunctionManager().getFunctionContaining(from);
                long fnRva = fn == null ? -1L
                        : fn.getEntryPoint().getOffset() - imageBase;
                pw.println("    xref@0x" + Long.toHexString(from.getOffset() - imageBase)
                        + " fn=" + (fn == null ? "<none>" : fn.getName())
                        + (fnRva < 0 ? "" : " rva=0x" + Long.toHexString(fnRva)));
                if (fnRva >= 0) {
                    found.put(fnRva, value);
                }
                shown++;
            }
        }
        pw.println();
        pw.println("named_functions=" + found.size());
        Set<Long> rvas = new LinkedHashSet<Long>();
        rvas.addAll(found.keySet());
        return rvas;
    }

    private boolean dumpFunction(long rva) {
        Address addr = toAbs(rva);
        Function fn = currentProgram.getFunctionManager().getFunctionContaining(addr);
        pw.println();
        pw.println("### rva=0x" + Long.toHexString(rva)
                + (fn == null ? " <no function>" : " fn=" + fn.getName()));
        if (fn == null) {
            return false;
        }
        pw.println("entry=0x" + Long.toHexString(fn.getEntryPoint().getOffset() - imageBase)
                + " size=0x" + Long.toHexString(fn.getBody().getNumAddresses()));
        pw.println("--- callers ---");
        ReferenceIterator refs = currentProgram.getReferenceManager()
                .getReferencesTo(fn.getEntryPoint());
        int shown = 0;
        while (refs.hasNext() && shown < 10) {
            Reference r = refs.next();
            Address from = r.getFromAddress();
            Function cf = currentProgram.getFunctionManager().getFunctionContaining(from);
            pw.println("    " + r.getReferenceType()
                    + " @0x" + Long.toHexString(from.getOffset() - imageBase)
                    + " fn=" + (cf == null ? "<none>" : cf.getName()));
            shown++;
        }
        DecompileResults results = decomp.decompileFunction(fn, 50, monitor);
        if (results.decompileCompleted()) {
            pw.println("--- decompiled ---");
            pw.println(results.getDecompiledFunction().getC());
        } else {
            pw.println("(decompile failed: " + results.getErrorMessage() + ")");
        }
        return true;
    }

    private Address toAbs(long rva) {
        return currentProgram.getAddressFactory()
                .getDefaultAddressSpace().getAddress(imageBase + rva);
    }

    private static String trim(String value, int max) {
        if (value.length() <= max) {
            return value;
        }
        return value.substring(0, max) + "...";
    }
}
