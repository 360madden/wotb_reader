// TraceAvatarHealthListener.java - resolve the avatar/vehicle health
// listener classes (HP-change notification hooks) and find the code that
// fires them: that code writes the new HP value first, so its call sites
// lead to the HP field location (playerHP discovery).
//
// Strategy:
//   1. Resolve the vftable symbol for AvatarHealthListener (and
//      VehicleHealthListener) from the RTTI_Type_Descriptor nearby.
//   2. Dump the listener vtable slots (the callback methods).
//   3. For each callback target, find callers (code that invokes the
//      listener = the HP-change publisher).
//   4. Report the publisher functions' windows for manual review.
//
// Hash-bound static evidence only; no live read, no promotion.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolType;

public class TraceAvatarHealthListener extends GhidraScript {

    private final List<String> notes = new ArrayList<String>();

    @Override
    public void run() throws Exception {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Memory memory = currentProgram.getMemory();

        String[] classNames = {
            "AvatarHealthListener", "VehicleHealthListener",
            "DeviceHealth", "TrackHealthEntityExtra"
        };

        for (String className : classNames) {
            out.append("### class: ").append(className).append("\n");
            String tdSym = className + "::RTTI_Type_Descriptor";
            String colSym = className + "::RTTI_Complete_Object_Locator";
            long colRva = resolveSymbolRva(colSym);
            out.append("col_symbol=").append(colSym)
                    .append(" rva=").append(colRva < 0 ? "none"
                            : "0x" + Long.toHexString(colRva)).append("\n");
            if (colRva < 0) {
                out.append("\n");
                continue;
            }
            // COL layout: [0]=signature [4]=offset [8]=cdOffset [12]=pType
            long pType = readU32(colRva + 12L);
            out.append("type_descriptor_ptr=0x")
                    .append(Long.toHexString(pType)).append("\n");

            // Find the vftable: MSVC emits <class>::vftable near the COL;
            // try the symbol first, else scan nearby .rdata for a pointer
            // back into .text (vtable slot 0 target).
            long vftableRva = resolveSymbolRva(className + "::vftable");
            if (vftableRva < 0) {
                vftableRva = findVftableNearCol(colRva);
            }
            out.append("vftable_rva=").append(vftableRva < 0 ? "none"
                    : "0x" + Long.toHexString(vftableRva)).append("\n");

            if (vftableRva >= 0) {
                out.append("vtable_slots:\n");
                for (int slot = 0; slot < 12; slot++) {
                    long slotAddr = vftableRva + (long) slot * 4L;
                    long target = readU32(slotAddr);
                    if (target <= 0 || target > 0x7000000L) {
                        out.append("  slot=").append(slot).append(" <end>\n");
                        break;
                    }
                    long targetRva = target - imageBase.getOffset();
                    Address t = imageBase.add(target);
                    Function fn = currentProgram.getFunctionManager()
                            .getFunctionAt(t);
                    Symbol sym = currentProgram.getSymbolTable()
                            .getPrimarySymbol(t);
                    out.append("  slot=").append(slot)
                            .append(" target_rva=0x")
                            .append(Long.toHexString(targetRva))
                            .append(" fn=").append(fn == null ? "<none>"
                                    : fn.getName())
                            .append(" sym=").append(sym == null ? "<none>"
                                    : sym.getName(true))
                            .append("\n");
                }
            }
            out.append("\n");
        }

        // Callers of the avatar health callback: the first vtable slot
        // (OnHealthChanged-ish). Resolve and scan .text for CALLs.
        long avatarVftable = resolveSymbolRva("AvatarHealthListener::vftable");
        if (avatarVftable < 0) {
            long col = resolveSymbolRva(
                    "AvatarHealthListener::RTTI_Complete_Object_Locator");
            avatarVftable = findVftableNearCol(col);
        }
        out.append("## callers of AvatarHealthListener slot0 (health-change publisher)\n");
        if (avatarVftable >= 0) {
            long target = readU32(avatarVftable);
            long targetRva = target - imageBase.getOffset();
            out.append("slot0_target_rva=0x")
                    .append(Long.toHexString(targetRva)).append("\n");
            List<Long> callers = findCallers(targetRva);
            out.append("caller_count=").append(callers.size()).append("\n");
            for (Long caller : callers) {
                Function fn = currentProgram.getFunctionManager()
                        .getFunctionContaining(imageBase.add(caller));
                out.append("caller_site=0x").append(Long.toHexString(caller))
                        .append(" fn=").append(fn == null ? "<none>"
                                : "0x" + Long.toHexString(
                                        fn.getEntryPoint().getOffset()
                                                - imageBase.getOffset())
                                + " " + fn.getName())
                        .append("\n");
            }
        } else {
            out.append("(avatar listener vftable not resolved)\n");
        }

        // The ListenerHolder<AvatarHealthListener> notify method iterates
        // the registered listeners and calls each callback through its
        // vtable (indirect CALL [reg+off]) — so the PUBLISHER calls the
        // holder's notify directly. Find the holder's vftable, enumerate
        // its methods, and scan .text for direct CALLs into each.
        out.append("## ListenerHolder<AvatarHealthListener> notify callers\n");
        long holderCol = resolveSymbolRva(
                "ListenerHolder<AvatarHealthListener>::RTTI_Complete_Object_Locator");
        out.append("holder_col_rva=").append(holderCol < 0 ? "none"
                : "0x" + Long.toHexString(holderCol)).append("\n");
        if (holderCol >= 0) {
            long holderVftable = resolveSymbolRva(
                    "ListenerHolder<AvatarHealthListener>::vftable");
            if (holderVftable < 0) {
                holderVftable = findVftableNearCol(holderCol);
            }
            out.append("holder_vftable_rva=").append(holderVftable < 0 ? "none"
                    : "0x" + Long.toHexString(holderVftable)).append("\n");
            if (holderVftable >= 0) {
                List<Long> holderMethods = new ArrayList<Long>();
                for (int slot = 0; slot < 20; slot++) {
                    long target = readU32(holderVftable + (long) slot * 4L);
                    long trva = target - imageBase.getOffset();
                    if (trva <= 0 || trva > 0x6000000L) {
                        break;
                    }
                    holderMethods.add(trva);
                }
                out.append("holder_method_count=").append(holderMethods.size())
                        .append("\n");
                for (Long m : holderMethods) {
                    List<Long> callers = findCallers(m);
                    if (callers.isEmpty()) {
                        continue;
                    }
                    out.append("holder_method=0x").append(Long.toHexString(m))
                            .append(" callers=")
                            .append(callers.size()).append("\n");
                    for (Long caller : callers) {
                        Function fn = currentProgram.getFunctionManager()
                                .getFunctionContaining(imageBase.add(caller));
                        out.append("  site=0x").append(Long.toHexString(caller))
                                .append(" fn=").append(fn == null ? "<none>"
                                        : "0x" + Long.toHexString(
                                                fn.getEntryPoint().getOffset()
                                                        - imageBase.getOffset())
                                        + " " + fn.getName())
                                .append("\n");
                    }
                }
            }
        }

        writeReport(out.toString());
        println("WROTE trace-avatar-health-listener.txt");
    }

    private long resolveSymbolRva(String qualified) {
        SymbolIterator symbols =
                currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext()) {
            Symbol symbol = symbols.next();
            if (symbol.getSymbolType() == SymbolType.LABEL
                    && qualified.equals(symbol.getName(true))) {
                return symbol.getAddress().getOffset()
                        - currentProgram.getImageBase().getOffset();
            }
        }
        return -1L;
    }

    private long readU32(long rva) {
        try {
            byte[] b = new byte[4];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getImageBase().add(rva), b);
            if (read != 4) {
                return -1L;
            }
            return (b[0] & 0xffL) | ((b[1] & 0xffL) << 8)
                    | ((b[2] & 0xffL) << 16) | ((b[3] & 0xffL) << 24);
        } catch (Exception e) {
            return -1L;
        }
    }

    private long findVftableNearCol(long colRva) {
        // vftable is usually immediately before the COL in .rdata (MSVC
        // emits COL, then the vftable follows with a 4-byte gap) or a few
        // hundred bytes away. Scan a tight window; require 4+ consecutive
        // pointers into .text (RVA range sane).
        long start = Math.max(0, colRva - 0x800);
        long end = colRva + 0x800;
        long best = -1L;
        for (long r = start; r < end; r += 4) {
            long v = readU32(r);
            long rva = v - currentProgram.getImageBase().getOffset();
            if (rva <= 0 || rva > 0x6000000L) {
                continue;
            }
            // check 3 consecutive .text pointers
            boolean ok = true;
            for (int i = 1; i < 4; i++) {
                long vv = readU32(r + (long) i * 4L);
                long rr = vv - currentProgram.getImageBase().getOffset();
                if (rr <= 0 || rr > 0x6000000L) {
                    ok = false;
                    break;
                }
            }
            if (ok) {
                return r;
            }
        }
        return -1L;
    }

    private List<Long> findCallers(long targetRva) {
        List<Long> callers = new ArrayList<Long>();
        Address imageBase = currentProgram.getImageBase();
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            byte[] data = new byte[(int) block.getSize()];
            int read;
            try {
                read = currentProgram.getMemory().getBytes(
                        block.getStart(), data);
            } catch (ghidra.program.model.mem.MemoryAccessException e) {
                continue;
            }
            if (read != data.length) {
                continue;
            }
            for (int i = 0; i + 4 < read; i++) {
                if (data[i] != (byte) 0xe8) {   // CALL rel32
                    continue;
                }
                long siteRva = block.getStart().add(i).getOffset()
                        - imageBase.getOffset();
                int rel = (data[i + 1] & 0xff)
                        | ((data[i + 2] & 0xff) << 8)
                        | ((data[i + 3] & 0xff) << 16)
                        | ((data[i + 4] & 0xff) << 24);
                long callTarget = siteRva + 5 + rel;
                if (callTarget == targetRva) {
                    callers.add(siteRva);
                }
            }
        }
        return callers;
    }

    private void writeReport(String body) throws Exception {
        String outPath = getEvidenceOutputPath("trace-avatar-health-listener.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.println("schema=wotbtreader.ghidra.trace-avatar-health-listener.v1");
        w.println("program=" + currentProgram.getName());
        w.println("executable_sha256=" + currentProgram.getExecutableSHA256());
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
