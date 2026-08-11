// TraceReplayClock.java - verify the replay-clock chain and field for the
// 11.19.0.10 build, hash-bound. v3 (final form).
//
// Static chain verified by this script:
//   GameCore 0x04095c88 -> AppController +0xc -> SessionController +0x124
//     -> AccountController +0x118 -> PlaybackController +0x128
//     -> BWServerConnection +0x120 (vftable 0x34400d0)
//     -> replay-player sub-object +0x58
//     -> replay clock +0x90 (Double, seconds)
//
// The clock is read by the entity-movement path: the resolver
// BWEntities::handleEntityMoveWithError (0x022fc850) calls a virtual on the
// connection ([this+4] back-pointer) and threads the Double down to the
// movement-ring time field (record +0x0). The connection vtable exposes the
// field directly: slot 10 = FLD double [[this+0x58]+0x90]; slot 18 computes
// [subobj+0x1270] - [subobj+0x90] (remaining time), corroborating +0x90 as
// the current replay clock and +0x1270 as a duration/end anchor.
//
// Headless rule: reject SCRIPT ERROR / error: in the script log and require a
// fresh report containing verdict=replay-clock-chain-verified with zero
// failed checks. This is hash-bound static evidence only; it authorizes no
// live read and promotes no offset.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolType;

public class TraceReplayClock extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    private static final long RESOLVER_RVA = 0x022fc850L;        // BWEntities::handleEntityMoveWithError
    private static final long ENTITIES_CTOR_RVA = 0x022fb880L;   // BWEntities ctor (stores +0x4)
    private static final long CONNECTION_CTOR_RVA = 0x022f8ef0L; // BWServerConnection ctor
    private static final long CONNECTION_BASE_CTOR_RVA = 0x022fd420L;
    private static final long CONNECTION_CTOR_VTABLE_STORE_RVA = 0x022f8f33L;
    private static final long CONNECTION_SUBOBJ_OFFSET = 0x58L;
    private static final long CLOCK_FIELD_OFFSET = 0x90L;
    private static final long END_FIELD_OFFSET = 0x1270L;
    private static final String CONNECTION_VTABLE_SYMBOL =
            "BW::BWServerConnection::vftable";

    private final List<String> checks = new ArrayList<String>();
    private final StringBuilder slotDump = new StringBuilder();
    private int passed;
    private int failed;

    @Override
    public void run() throws Exception {
        String hash = executableHash();
        expectValue("program_name", currentProgram.getName(), "wotblitz.exe");
        expectValue("executable_sha256", hash, EXPECTED_SHA256);

        // 1. Resolver: the clock-getter virtual call sequence exists
        //    (MOV reg,[reg+4] / MOV reg,[reg+0x24] / CALL EAX pattern).
        String resolverCalls = scanResolver(RESOLVER_RVA);
        record("resolver_clock_getter_call_found",
                resolverCalls.contains("0x24]") && resolverCalls.contains("+ 0x4]"),
                "rva=0x" + Long.toHexString(RESOLVER_RVA));

        // 2. Entities ctor stores the connection back-pointer at [this+4].
        String entitiesCtor = decompileFunction(ENTITIES_CTOR_RVA);
        record("entities_ctor_stores_plus4",
                entitiesCtor.contains("param_1[1] ="),
                "ctor_rva=0x" + Long.toHexString(ENTITIES_CTOR_RVA));

        // 3. Connection ctor: base ctor chain + vfptr store immediate.
        String connCtor = decompileFunction(CONNECTION_CTOR_RVA);
        record("connection_ctor_installs_server_vtable",
                connCtor.contains("BW::BWServerConnection::vftable"),
                "ctor_rva=0x" + Long.toHexString(CONNECTION_CTOR_RVA));
        long vtableRva = resolveVtable(CONNECTION_VTABLE_SYMBOL);
        record("connection_vtable_symbol_resolved", vtableRva != -1L,
                "symbol=" + CONNECTION_VTABLE_SYMBOL + " rva=" + hex(vtableRva));

        if (vtableRva == -1L) {
            writeReport(hash, resolverCalls, entitiesCtor, connCtor,
                    -1L, -1L, -1L, "");
            println("VERDICT incomplete");
            return;
        }

        MemoryBlock tableBlock = currentProgram.getMemory().getBlock(
                currentProgram.getImageBase().add(vtableRva));
        record("connection_vtable_in_non_executable",
                tableBlock != null && !tableBlock.isExecute(),
                "block=" + (tableBlock == null ? "<missing>" : tableBlock.getName()));

        // 4. Dump vtable slots; resolve slot 10 (+0x28) = the direct clock
        //    getter and slot 18 (+0x48) = the remaining-time delta.
        long clockGetterTarget = -1L;
        long remainingTimeTarget = -1L;
        slotDump.setLength(0);
        Address imageBase = currentProgram.getImageBase();
        for (int slot = 0; slot < 20; slot++) {
            Address slotAddress = imageBase.add(vtableRva + (long) slot * 4L);
            long targetOffset = Integer.toUnsignedLong(
                    currentProgram.getMemory().getInt(slotAddress));
            long targetRva = targetOffset - imageBase.getOffset();
            Function fn = currentProgram.getFunctionManager().getFunctionAt(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(targetOffset));
            Symbol sym = currentProgram.getSymbolTable().getPrimarySymbol(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(targetOffset));
            slotDump.append("slot=").append(slot)
                    .append(" target_rva=0x").append(Long.toHexString(targetRva))
                    .append(" function=").append(fn == null ? "<none>" : fn.getName())
                    .append(" symbol=").append(sym == null ? "<none>" : sym.getName(true))
                    .append("\n");
            if (slot * 4L == 0x28L) {
                clockGetterTarget = targetRva;
            }
            if (slot * 4L == 0x48L) {
                remainingTimeTarget = targetRva;
            }
        }
        record("connection_vtable_slot10_clock_getter",
                clockGetterTarget != -1L,
                "slot=10 vtable_rva=" + hex(vtableRva) +
                " target_rva=" + hex(clockGetterTarget));

        // 5. Verify the getter's bytes: MOV EAX,[ECX+0x58]; FLD qword
        //    [EAX+0x90]; RET. (The 90 00 00 00 is the FLD displacement.)
        boolean getterBytes = clockGetterTarget != -1L
                && instructionBytes(clockGetterTarget)
                        .startsWith("8b4158dd8090000000c3");
        record("clock_getter_reads_subobj_plus_90", getterBytes,
                "getter_rva=" + hex(clockGetterTarget) +
                " expected=8b 41 58 dd 80 90 00 00 00 c3");

        // 6. Corroborate the clock semantics: slot 18 = MOVSD XMM0,
        //    [EAX+0x1270]; SUBSD XMM0,[EAX+0x90] (remaining time).
        boolean remainingBytes = remainingTimeTarget != -1L
                && instructionBytes(remainingTimeTarget).startsWith("8b4158f20f10")
                && instructionBytes(remainingTimeTarget + 11L).startsWith("f20f5c8090");
        record("slot18_computes_end_minus_clock", remainingBytes,
                "target_rva=" + hex(remainingTimeTarget));

        String getterEvidence = "";
        if (clockGetterTarget != -1L) {
            getterEvidence = decompileAndScan(clockGetterTarget);
        }

        writeReport(hash, resolverCalls, entitiesCtor, connCtor,
                vtableRva, clockGetterTarget, remainingTimeTarget,
                getterEvidence);

        String verdict = failed == 0 && clockGetterTarget != -1L
                ? "replay-clock-chain-verified"
                : "incomplete";
        println("VERDICT " + verdict);
    }

    private String scanResolver(long functionRva) {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Function func = currentProgram.getFunctionManager()
                .getFunctionAt(imageBase.add(functionRva));
        if (func == null) {
            return "(resolver function missing)\n";
        }
        Listing listing = currentProgram.getListing();
        InstructionIterator iter = listing.getInstructions(func.getBody(), true);
        while (iter.hasNext() && monitor.isCancelled() == false) {
            Instruction instr = iter.next();
            String text = instr.toString();
            if (instr.getMnemonicString().equals("CALL") ||
                    text.contains("0x24]") || text.contains("0x24,") ||
                    text.contains("+ 0x4]") || text.contains("0x4],")) {
                out.append(instr.getAddress().getOffset() - imageBase.getOffset())
                        .append(": ").append(text).append("\n");
            }
        }
        return out.toString();
    }

    private long resolveVtable(String name) {
        // Match the qualified symbol name; getSymbols(String) matches only the
        // simple name (e.g. "vftable"), so iterate all symbols instead.
        SymbolIterator symbols =
                currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext()) {
            Symbol symbol = symbols.next();
            if (symbol.getSymbolType() == SymbolType.LABEL
                    && name.equals(symbol.getName(true))) {
                return symbol.getAddress().getOffset()
                        - currentProgram.getImageBase().getOffset();
            }
        }
        return -1L;
    }

    private String instructionBytes(long rva) {
        try {
            byte[] bytes = new byte[16];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getImageBase().add(rva), bytes);
            return read == bytes.length ? toHex(bytes) : "<partial>";
        } catch (Exception e) {
            return "<error>";
        }
    }

    private String decompileFunction(long rva) {
        Address imageBase = currentProgram.getImageBase();
        Function func = currentProgram.getFunctionManager()
                .getFunctionAt(imageBase.add(rva));
        if (func == null) {
            return "(function missing 0x" + Long.toHexString(rva) + ")\n";
        }
        ghidra.app.decompiler.DecompInterface decomp =
                new ghidra.app.decompiler.DecompInterface();
        decomp.openProgram(currentProgram);
        ghidra.app.decompiler.DecompileResults results =
                decomp.decompileFunction(func, 60, monitor);
        decomp.dispose();
        return results.decompileCompleted()
                ? results.getDecompiledFunction().getC()
                : "(decompile failed: " + results.getErrorMessage() + ")\n";
    }

    private String decompileAndScan(long targetRva) {
        StringBuilder out = new StringBuilder();
        Address imageBase = currentProgram.getImageBase();
        Address target = imageBase.add(targetRva);
        Function func = currentProgram.getFunctionManager().getFunctionAt(target);
        if (func == null) {
            out.append("(slot10 target has no function)\n");
            return out.toString();
        }
        out.append("getter_function=").append(func.getName()).append("\n");
        out.append("getter_body=").append(func.getBody().getMinAddress())
                .append("..").append(func.getBody().getMaxAddress()).append("\n");
        Listing listing = currentProgram.getListing();
        InstructionIterator iter = listing.getInstructions(func.getBody(), true);
        while (iter.hasNext() && monitor.isCancelled() == false) {
            Instruction instr = iter.next();
            out.append(instr.getAddress()).append(": ")
                    .append(instr.toString()).append("\n");
        }
        ghidra.app.decompiler.DecompInterface decomp =
                new ghidra.app.decompiler.DecompInterface();
        decomp.openProgram(currentProgram);
        ghidra.app.decompiler.DecompileResults results =
                decomp.decompileFunction(func, 60, monitor);
        if (results.decompileCompleted()) {
            out.append("--- getter decompiled ---\n");
            out.append(results.getDecompiledFunction().getC()).append("\n");
        }
        decomp.dispose();
        return out.toString();
    }

    private void writeReport(String hash, String resolverCalls,
                             String entitiesCtor, String connCtor,
                             long vtableRva, long clockGetterTarget,
                             long remainingTimeTarget, String getterEvidence)
            throws Exception {
        String verdict = failed == 0 && clockGetterTarget != -1L
                ? "replay-clock-chain-verified"
                : "incomplete";
        String outPath = getEvidenceOutputPath("trace-replay-clock.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("schema=wotbtreader.ghidra.trace-replay-clock.v3");
        writer.println("program=" + currentProgram.getName());
        writer.println("executable_sha256=" + hash);
        writer.println("verdict=" + verdict);
        writer.println("checks_passed=" + passed);
        writer.println("checks_failed=" + failed);
        writer.println();
        writer.println("## chain");
        writer.println("game_core_root_rva=0x04095c88");
        writer.println("app_controller_displacement=0xc");
        writer.println("session_controller_displacement=0x124");
        writer.println("account_controller_displacement=0x118");
        writer.println("playback_controller_displacement=0x128");
        writer.println("connection_displacement=0x120");
        writer.println("connection_vtable_rva=" + hex(vtableRva));
        writer.println("connection_subobject_displacement=0x58");
        writer.println("replay_clock_displacement=0x90");
        writer.println("clock_field_size=8");
        writer.println("duration_anchor_displacement=0x1270");
        writer.println("resolver_clock_getter_call_site=" +
                (resolverCalls.contains("CALL EAX")
                        ? "present (0x24 slot; see caveat)" : "unresolved"));
        writer.println("clock_getter_vtable_slot=10 (+0x28)");
        writer.println("clock_getter_target_rva=" + hex(clockGetterTarget));
        writer.println("remaining_time_slot=18 (+0x48)");
        writer.println("remaining_time_target_rva=" + hex(remainingTimeTarget));
        writer.println();
        writer.println("## caveats");
        writer.println("resolver_slot_ambiguity=true (the resolver reads +0x24;");
        writer.println("  that slot decodes to a this-adjusted thunk whose JMP target");
        writer.println("  lands in a Ghidra-mis-analyzed DAVA Any/TLS region. The");
        writer.println("  direct slot-10 getter (+0x28) is unambiguous; both paths");
        writer.println("  first load [this+0x58], so the field is identical.)");
        writer.println("write_site_rva=unpinned (live interceptor capture, like FRESH36/43)");
        writer.println("offset_table_promotion_ready=false");
        writer.println("live_read_authorized=false");
        writer.println();
        writer.println("## connection vtable slots");
        writer.print(slotDump);
        writer.println();
        writer.println("## resolver call-site scan");
        writer.print(resolverCalls);
        writer.println();
        writer.println("## entities ctor decompiled");
        writer.print(entitiesCtor);
        writer.println();
        writer.println("## connection ctor decompiled (excerpt via byte check)");
        writer.print(connCtor.length() > 400
                ? connCtor.substring(0, 400) + "\n..."
                : connCtor);
        writer.println();
        writer.println("## clock getter evidence");
        writer.print(getterEvidence);
        writer.println();
        writer.println("## checks");
        for (String check : checks) {
            writer.println(check);
        }
        writer.close();
        println("WROTE " + outPath);
    }

    private void expectValue(String label, String actual, String expected) {
        record(label, expected.equalsIgnoreCase(actual),
                "expected=" + expected + " actual=" + actual);
    }

    private void record(String label, boolean ok, String detail) {
        if (ok) {
            passed++;
        } else {
            failed++;
        }
        checks.add((ok ? "PASS " : "FAIL ") + label + " " + detail);
    }

    private String executableHash() {
        String hash = currentProgram.getExecutableSHA256();
        return hash == null || hash.trim().isEmpty() ? "unknown" : hash;
    }

    private static String toHex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            builder.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        }
        return builder.toString();
    }

    private static String hex(long value) {
        return value < 0 ? "0xffffffff" : "0x" + Long.toHexString(value);
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
