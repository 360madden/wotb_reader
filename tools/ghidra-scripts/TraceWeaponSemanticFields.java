// TraceWeaponSemanticFields.java - hash-bound write/read-site trace for
// VehicleGun / VehicleGunRotator / AvatarGunAgent candidate fields.
//
// Ownership is already live-proven. This pass only ranks constructor
// offsets as gun/shell/aim/ray candidates. It does not promote offsets.
//
// Listing-confirmed only: instruction-boundary displacements, never an
// unaligned byte walk (that class of false positive is retired).
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript TraceWeaponSemanticFields.java \
//       -scriptPath <repo>\tools\ghidra-scripts

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.Symbol;

public class TraceWeaponSemanticFields extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";
    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;
    private static final int MAX_VTABLE_SLOTS = 40;
    private static final int MAX_DECOMPILE = 28;
    private static final int MAX_STRING_XREFS = 24;

    private static final long GUN_VFT = 0x32dacf4L;
    private static final long ROT_VFT = 0x32eeb40L;
    private static final long AGENT_VFT = 0x324dae8L;

    private static final long[] GUN_FIELDS = {
        0x38L, 0x3cL, 0x40L, 0x44L, 0x48L, 0x4cL, 0x50L, 0x54L, 0x58L, 0x5cL, 0x60L
    };
    private static final long[] ROT_FIELDS = {
        0x50L, 0x84L, 0x88L, 0x8cL, 0xecL, 0x130L, 0x134L, 0x138L, 0x13cL,
        0x148L, 0x1a8L, 0x1acL, 0x1b0L, 0x1b4L, 0x1b8L, 0x1bcL, 0x1c0L
    };

    private static final long[] KNOWN_CTOR_DTOR = {
        0x199cc30L, 0x19a8bb0L, 0x19b0ff0L, 0x1ab6ba0L, 0x1ab9c50L, 0xf60360L
    };

    private static final String[] STRING_NEEDLES = {
        "shell", "Shell", "ammo", "Ammo", "reload", "clip", "magazine",
        "turret", "Turret", "elevation", "gunYaw", "gunPitch", "yaw",
        "pitch", "muzzle", "shotDispersion", "currentShell", "piercing",
        "VehicleGun", "VehicleGunRotator", "AvatarGunAgent", "FollowAim"
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
        pw = new PrintWriter(new File(dir, "weapon-semantic-fields.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.trace-weapon-semantic-fields.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + actual);
        pw.println("hash_match=true");
        pw.println();

        imageBase = currentProgram.getImageBase().getOffset();
        decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Set<Long> gunSlots = dumpVtable("VehicleGun", GUN_VFT);
        Set<Long> rotSlots = dumpVtable("VehicleGunRotator", ROT_VFT);
        Set<Long> agentSlots = dumpVtable("AvatarGunAgent", AGENT_VFT);

        Map<Long, List<Hit>> gunHits = collectHits(GUN_FIELDS, gunSlots, "VehicleGun");
        Map<Long, List<Hit>> rotHits = collectHits(ROT_FIELDS, rotSlots, "VehicleGunRotator");
        writeHits("VehicleGun candidate-field sites", gunHits);
        writeHits("VehicleGunRotator candidate-field sites", rotHits);

        Set<Long> writerRvas = new LinkedHashSet<Long>();
        collectWriterRvas(gunHits, writerRvas);
        collectWriterRvas(rotHits, writerRvas);
        writerRvas.addAll(gunSlots);
        writerRvas.addAll(agentSlots);

        pw.println();
        pw.println("============================================================");
        pw.println("## decompiled writers / gun+agent methods");
        pw.println("============================================================");
        int decompiled = 0;
        for (Long rva : writerRvas) {
            if (decompiled >= MAX_DECOMPILE) {
                pw.println("(decompile cap " + MAX_DECOMPILE + " reached; remaining RVAs omitted)");
                break;
            }
            if (isKnownCtorDtor(rva)) {
                pw.println();
                pw.println("### skip ctor/dtor rva=0x" + Long.toHexString(rva));
                continue;
            }
            if (dumpFunction(rva)) {
                decompiled++;
            }
        }

        writeStringNeedles();

        decomp.dispose();
        pw.close();
        println("WROTE " + new File(dir, "weapon-semantic-fields.txt").getAbsolutePath());
    }

    private Set<Long> dumpVtable(String name, long rva) throws Exception {
        pw.println("============================================================");
        pw.println("## vtable " + name + " rva=0x" + Long.toHexString(rva));
        pw.println("============================================================");
        Set<Long> slots = new LinkedHashSet<Long>();
        Address table = toAbs(rva);
        Symbol tableSymbol = currentProgram.getSymbolTable().getPrimarySymbol(table);
        pw.println("vtable_symbol=" + (tableSymbol == null ? "<none>" : tableSymbol.getName(true)));
        for (int slot = 0; slot < MAX_VTABLE_SLOTS; slot++) {
            Address slotAddr = table.add((long) slot * 4L);
            long targetAbs = Integer.toUnsignedLong(currentProgram.getMemory().getInt(slotAddr));
            if (targetAbs < CODE_LO || targetAbs > CODE_HI) {
                pw.println("slot=" + slot + " stop non-code target=0x" + Long.toHexString(targetAbs));
                break;
            }
            long targetRva = targetAbs - imageBase;
            Address target = currentProgram.getAddressFactory()
                    .getDefaultAddressSpace().getAddress(targetAbs);
            Function fn = currentProgram.getFunctionManager().getFunctionAt(target);
            if (fn == null) {
                pw.println("slot=" + slot + " stop no-function rva=0x" + Long.toHexString(targetRva));
                break;
            }
            slots.add(fn.getEntryPoint().getOffset() - imageBase);
            pw.println("slot=" + slot
                    + " rva=0x" + Long.toHexString(targetRva)
                    + " fn=" + fn.getName()
                    + " size=0x" + Long.toHexString(fn.getBody().getNumAddresses()));
        }
        pw.println("slot_functions=" + slots.size());
        pw.println();
        return slots;
    }

    private Map<Long, List<Hit>> collectHits(long[] fields, Set<Long> familyFns, String family)
            throws Exception {
        Set<Long> wanted = new LinkedHashSet<Long>();
        for (long f : fields) {
            wanted.add(f);
        }
        Map<Long, List<Hit>> hits = new TreeMap<Long, List<Hit>>();
        for (long f : fields) {
            hits.put(f, new ArrayList<Hit>());
        }

        Listing listing = currentProgram.getListing();
        AddressSet exec = new AddressSet();
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (block.isExecute()) {
                exec.add(block.getStart(), block.getEnd());
            }
        }
        for (Instruction insn : listing.getInstructions(exec, true)) {
            Long dest = displacement(insn, 0);
            Long src = insn.getNumOperands() > 1 ? displacement(insn, 1) : null;
            if (dest != null && wanted.contains(dest)) {
                addHit(hits.get(dest), insn, familyFns, family, "write");
            }
            if (src != null && wanted.contains(src)) {
                addHit(hits.get(src), insn, familyFns, family, "read");
            }
        }
        return hits;
    }

    private void addHit(List<Hit> list, Instruction insn, Set<Long> familyFns,
            String family, String kind) {
        Address from = insn.getAddress();
        Function fn = currentProgram.getFunctionManager().getFunctionContaining(from);
        long siteRva = from.getOffset() - imageBase;
        long fnRva = fn == null ? -1L : fn.getEntryPoint().getOffset() - imageBase;
        boolean inFamily = fnRva >= 0 && familyFns.contains(fnRva);
        boolean ctor = isKnownCtorDtor(fnRva);
        list.add(new Hit(siteRva, fnRva,
                fn == null ? "<none>" : fn.getName(),
                kind, inFamily, ctor, insn.toString().replace('\n', ' ')));
    }

    private void writeHits(String title, Map<Long, List<Hit>> hits) {
        pw.println("============================================================");
        pw.println("## " + title);
        pw.println("============================================================");
        for (Map.Entry<Long, List<Hit>> e : hits.entrySet()) {
            List<Hit> all = e.getValue();
            int family = 0;
            int nonCtorFamily = 0;
            for (Hit h : all) {
                if (h.inFamily) {
                    family++;
                    if (!h.ctor) {
                        nonCtorFamily++;
                    }
                }
            }
            pw.println();
            pw.println("### +0x" + Long.toHexString(e.getKey())
                    + " total=" + all.size()
                    + " family=" + family
                    + " family_non_ctor=" + nonCtorFamily);
            int shown = 0;
            for (Hit h : all) {
                if (!h.inFamily) {
                    continue;
                }
                if (shown >= 20) {
                    pw.println("    (more family hits omitted)");
                    break;
                }
                pw.println("    " + h.kind
                        + " site=0x" + Long.toHexString(h.siteRva)
                        + " fn=0x" + Long.toHexString(h.fnRva)
                        + " " + h.fnName
                        + (h.ctor ? " [ctor/dtor]" : "")
                        + "  " + h.text);
                shown++;
            }
            if (shown == 0) {
                pw.println("    (no listing-confirmed family hits)");
            }
        }
        pw.println();
    }

    private void collectWriterRvas(Map<Long, List<Hit>> hits, Set<Long> dest) {
        for (List<Hit> list : hits.values()) {
            for (Hit h : list) {
                if (h.inFamily && !h.ctor && h.fnRva >= 0 && "write".equals(h.kind)) {
                    dest.add(h.fnRva);
                }
            }
        }
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
        while (refs.hasNext() && shown < 12) {
            Reference r = refs.next();
            if (!r.getReferenceType().isCall() && !r.getReferenceType().isData()) {
                continue;
            }
            Address from = r.getFromAddress();
            Function cf = currentProgram.getFunctionManager().getFunctionContaining(from);
            pw.println("    " + r.getReferenceType()
                    + " @0x" + Long.toHexString(from.getOffset() - imageBase)
                    + " fn=" + (cf == null ? "<none>" : cf.getName()));
            shown++;
        }
        if (shown == 0) {
            pw.println("    (no call/data refs shown)");
        }
        DecompileResults results = decomp.decompileFunction(fn, 45, monitor);
        if (results.decompileCompleted()) {
            pw.println("--- decompiled ---");
            pw.println(results.getDecompiledFunction().getC());
        } else {
            pw.println("(decompile failed: " + results.getErrorMessage() + ")");
        }
        return true;
    }

    private void writeStringNeedles() {
        pw.println();
        pw.println("============================================================");
        pw.println("## string needles");
        pw.println("============================================================");
        Listing listing = currentProgram.getListing();
        Map<String, List<String>> found = new LinkedHashMap<String, List<String>>();
        for (String needle : STRING_NEEDLES) {
            found.put(needle, new ArrayList<String>());
        }
        for (Data data : listing.getDefinedData(true)) {
            if (!data.hasStringValue()) {
                continue;
            }
            String value = data.getDefaultValueRepresentation();
            if (value == null) {
                continue;
            }
            for (String needle : STRING_NEEDLES) {
                if (value.indexOf(needle) < 0) {
                    continue;
                }
                List<String> rows = found.get(needle);
                if (rows.size() >= 8) {
                    continue;
                }
                StringBuilder row = new StringBuilder();
                row.append("str@0x").append(Long.toHexString(data.getAddress().getOffset() - imageBase))
                        .append(" ").append(trim(value, 80));
                ReferenceIterator refs = currentProgram.getReferenceManager()
                        .getReferencesTo(data.getAddress());
                int n = 0;
                while (refs.hasNext() && n < MAX_STRING_XREFS) {
                    Reference r = refs.next();
                    Address from = r.getFromAddress();
                    Function fn = currentProgram.getFunctionManager().getFunctionContaining(from);
                    row.append(" | xref@0x").append(Long.toHexString(from.getOffset() - imageBase))
                            .append(" fn=").append(fn == null ? "<none>" : fn.getName());
                    n++;
                }
                rows.add(row.toString());
            }
        }
        for (Map.Entry<String, List<String>> e : found.entrySet()) {
            pw.println();
            pw.println("### needle=" + e.getKey() + " hits=" + e.getValue().size());
            if (e.getValue().isEmpty()) {
                pw.println("    (none)");
                continue;
            }
            for (String row : e.getValue()) {
                pw.println("    " + row);
            }
        }
    }

    private Long displacement(Instruction insn, int opIndex) {
        if (opIndex >= insn.getNumOperands()) {
            return null;
        }
        Object[] objs = insn.getOpObjects(opIndex);
        if (objs == null || objs.length == 0) {
            return null;
        }
        Long last = null;
        int scalars = 0;
        boolean memish = insn.getDefaultOperandRepresentation(opIndex).indexOf('[') >= 0;
        if (!memish) {
            return null;
        }
        for (Object o : objs) {
            if (o instanceof Scalar) {
                scalars++;
                last = Long.valueOf(((Scalar) o).getSignedValue());
            }
        }
        if (scalars != 1 || last == null) {
            return null;
        }
        long v = last.longValue();
        if (v < 0 || v > 0x200L) {
            return null;
        }
        return last;
    }

    private boolean isKnownCtorDtor(long rva) {
        for (long known : KNOWN_CTOR_DTOR) {
            if (known == rva) {
                return true;
            }
        }
        return false;
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

    private static final class Hit {
        final long siteRva;
        final long fnRva;
        final String fnName;
        final String kind;
        final boolean inFamily;
        final boolean ctor;
        final String text;

        Hit(long siteRva, long fnRva, String fnName, String kind,
                boolean inFamily, boolean ctor, String text) {
            this.siteRva = siteRva;
            this.fnRva = fnRva;
            this.fnName = fnName;
            this.kind = kind;
            this.inFamily = inFamily;
            this.ctor = ctor;
            this.text = text;
        }
    }
}
