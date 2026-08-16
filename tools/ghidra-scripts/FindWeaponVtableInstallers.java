// FindWeaponVtableInstallers.java - locate the .text sites that install the
// VehicleGun / VehicleGunRotator / AvatarGunAgent vftables and dump the
// enclosing constructor + its callers, so the viewpoint-vehicle ownership
// walk has bounded, hash-bound static evidence.
//
// Matches both the RVA immediate and the RVA+0x400000 immediate because prior
// scans observed the RVA form (MOV dword ptr [ESI],0x36752a4) in this project.
//
//   analyzeHeadless.bat C:\work\tools\ghidra-projects WotBlitz \
//       -process wotblitz.exe -noanalysis \
//       -postScript FindWeaponVtableInstallers.java \
//       -scriptPath C:\work\wotb_reader\tools\ghidra-scripts \
//       -scriptlog C:\work\wotb_reader\.build\ghidra-weapon-install.log

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashMap;
import java.util.Map;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class FindWeaponVtableInstallers extends GhidraScript {

    private static final long CODE_LO = 0x00400000L;
    private static final long CODE_HI = 0x03000000L;
    private static final long BASE_DELTA = 0x400000L;

    // vftable RVAs derived hash-bound via FindVftableViaCol (11.19.0.10).
    private static final long[] VFTABLES = {
        0x32dacf4L, // VehicleGun
        0x32eeb40L, // VehicleGunRotator
        0x324dae8L  // AvatarGunAgent
    };

    private PrintWriter pw;

    @Override
    public void run() throws Exception {
        String outDir = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        if (outDir == null || outDir.trim().isEmpty()) {
            outDir = "C:\\work\\wotb_reader\\.build\\ghidra-evidence-weapon-install";
        }
        File dir = new File(outDir);
        if (!dir.exists() && !dir.mkdirs()) {
            throw new IllegalStateException("Could not create " + dir);
        }
        pw = new PrintWriter(new File(dir, "weapon-vtable-installers.txt"), "UTF-8");
        pw.println("schema=wotbtreader.ghidra.find-weapon-vtable-installers.v1");
        pw.println("program=" + currentProgram.getName());
        pw.println("executable_sha256=" + currentProgram.getExecutableSHA256());
        pw.println();

        Listing listing = currentProgram.getListing();
        FunctionManager fm = currentProgram.getFunctionManager();
        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        AddressSet range = new AddressSet(toAddr(CODE_LO), toAddr(CODE_HI));
        for (long vftRva : VFTABLES) {
            pw.println("============================================================");
            pw.println("## vftable RVA 0x" + Long.toHexString(vftRva));
            pw.println("============================================================");
            // candidate immediates: RVA and RVA + preferred base
            long[] imms = { vftRva, vftRva + BASE_DELTA };
            Map<String, SiteInfo> sites = new LinkedHashMap<String, SiteInfo>();
            for (Instruction insn : listing.getInstructions(range, true)) {
                String mnem = insn.getMnemonicString();
                if (!"MOV".equals(mnem)) {
                    continue;
                }
                if (insn.getNumOperands() < 2) {
                    continue;
                }
                Object[] refs = insn.getOpObjects(1);
                if (refs == null || refs.length == 0) {
                    continue;
                }
                long v = -1;
                if (refs[0] instanceof Scalar) {
                    v = ((Scalar) refs[0]).getUnsignedValue();
                } else {
                    continue;
                }
                boolean matched = false;
                for (long imm : imms) {
                    if (v == imm) {
                        matched = true;
                        break;
                    }
                }
                if (!matched) {
                    continue;
                }
                Address from = insn.getAddress();
                Function f = fm.getFunctionContaining(from);
                long siteRva = from.getOffset() - currentProgram.getImageBase().getOffset();
                String key = (f == null ? "<none>" : f.getName()) + "@"
                        + (f == null ? "0" : Long.toHexString(
                                f.getEntryPoint().getOffset()
                                        - currentProgram.getImageBase().getOffset()));
                SiteInfo info = sites.get(key);
                if (info == null) {
                    info = new SiteInfo(f, key);
                    sites.put(key, info);
                }
                info.add(siteRva, insn.toString(), v);
            }

            pw.println("installer functions: " + sites.size());
            for (SiteInfo info : sites.values()) {
                pw.println();
                pw.println("### installer fn " + info.key);
                for (String line : info.sites) {
                    pw.println("    " + line);
                }
                if (info.fn == null) {
                    pw.println("    (no enclosing function)");
                    continue;
                }
                pw.println("    entry=0x" + Long.toHexString(
                        info.fn.getEntryPoint().getOffset()
                                - currentProgram.getImageBase().getOffset()));
                DecompileResults results = decomp.decompileFunction(info.fn, 60, monitor);
                if (results.decompileCompleted()) {
                    pw.println("--- decompiled ---");
                    pw.println(results.getDecompiledFunction().getC());
                } else {
                    pw.println("(decompile failed: " + results.getErrorMessage() + ")");
                }
                pw.println("--- callers ---");
                ReferenceIterator refs = currentProgram.getReferenceManager()
                        .getReferencesTo(info.fn.getEntryPoint());
                int shown = 0;
                while (refs.hasNext() && shown < 16) {
                    Reference r = refs.next();
                    if (!r.getReferenceType().isCall()) {
                        continue;
                    }
                    Address from = r.getFromAddress();
                    Function cf = fm.getFunctionContaining(from);
                    pw.println("    CALL @0x" + Long.toHexString(
                            from.getOffset() - currentProgram.getImageBase().getOffset())
                            + " fn=" + (cf == null ? "<none>" : cf.getName()));
                    shown++;
                }
                if (shown == 0) {
                    pw.println("    (no call callers)");
                }
            }
            pw.println();
        }

        decomp.dispose();
        pw.close();
        println("WROTE " + new File(dir, "weapon-vtable-installers.txt").getAbsolutePath());
    }

    private static final class SiteInfo {
        final Function fn;
        final String key;
        final java.util.List<String> sites = new java.util.ArrayList<String>();
        SiteInfo(Function fn, String key) {
            this.fn = fn;
            this.key = key;
        }
        void add(long siteRva, String text, long imm) {
            sites.add("site=0x" + Long.toHexString(siteRva)
                    + " imm=0x" + Long.toHexString(imm) + " " + text);
        }
    }
}
