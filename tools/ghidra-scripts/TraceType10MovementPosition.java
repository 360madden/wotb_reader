// TraceType10MovementPosition.java - verify the current-build replay type-10
// position packet's static path into the BW entity movement application.
//
// This is deliberately hash-bound. It does not discover an offset and it does
// not authorize live capture. It proves or refutes a fixed set of instruction,
// direct-call, and vtable relationships in the already analyzed executable.
// Evidence is written only to the ignored .build/ghidra-evidence directory.
//
// Important headless rule: Ghidra can return exit zero after a script compile
// or runtime error. Callers must also reject SCRIPT ERROR/error: in the script
// log, require a fresh report, and require verdict=semantic-chain-proven.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;

public class TraceType10MovementPosition extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    private static final long REPLAY_TABLE_TYPE10_ASSIGN_RVA = 0x00fbc1d4L;
    private static final long TYPE10_HANDLER_RVA = 0x00fe31c0L;
    private static final long REPLAY_READ_BYTES_RVA = 0x010012d0L;
    private static final long BLITZ_HANDLER_VTABLE_RVA = 0x0324dd90L;
    private static final int BLITZ_MOVE_SLOT = 13;
    private static final long BLITZ_MOVE_RVA = 0x00f7a610L;
    private static final long ENGINE_FORWARD_RVA = 0x022f9710L;
    private static final long ENTITY_RESOLVE_RVA = 0x022fc850L;
    private static final long ENTITY_APPLY_RVA = 0x022fa780L;
    private static final long ENTITY_POSITION_ANCHOR_RVA = 0x022fa78dL;
    private static final long AVATAR_FILTER_VTABLE_RVA = 0x03442520L;
    private static final long AVATAR_FILTER_APPLY_RVA = 0x0230e8f0L;
    private static final long AVATAR_HELPER_VTABLE_RVA = 0x034424a4L;
    private static final long AVATAR_HELPER_STORE_RVA = 0x0230df40L;

    private final List<String> checks = new ArrayList<String>();
    private int passed;
    private int failed;

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        String hash = executableHash();

        expectValue("program_name", currentProgram.getName(), "wotblitz.exe");
        expectValue("executable_sha256", hash, EXPECTED_SHA256);

        expectInstruction(
                "replay_table_type10_assignment",
                REPLAY_TABLE_TYPE10_ASSIGN_RVA,
                "MOV dword ptr [EBP + 0xffffff7c],0x13e31c0",
                "c7857cffffffc0313e01");

        long[] lengthPushRvas = {
            0x00fe31cdL, 0x00fe31dcL, 0x00fe31e9L,
            0x00fe31fbL, 0x00fe3212L,
            0x00fe3224L, 0x00fe3231L, 0x00fe323eL,
            0x00fe324bL
        };
        int[] lengths = { 4, 4, 4, 12, 12, 4, 4, 4, 1 };
        long[] readCallRvas = {
            0x00fe31d2L, 0x00fe31dfL, 0x00fe31ecL,
            0x00fe3203L, 0x00fe321aL,
            0x00fe3227L, 0x00fe3234L, 0x00fe3241L,
            0x00fe324eL
        };
        for (int index = 0; index < lengths.length; index++) {
            expectInstructionContains(
                    "type10_length_" + index,
                    lengthPushRvas[index],
                    "PUSH 0x" + Integer.toHexString(lengths[index]));
            expectDirectCall(
                    "type10_read_call_" + index,
                    readCallRvas[index],
                    REPLAY_READ_BYTES_RVA);
        }
        expectInstructionContains(
                "type10_blitz_move_dispatch",
                0x00fe328fL,
                "CALL dword ptr [EAX + 0x34]");
        expectVtableSlot(
                "blitz_move_slot",
                BLITZ_HANDLER_VTABLE_RVA,
                BLITZ_MOVE_SLOT,
                BLITZ_MOVE_RVA);

        expectDirectCall(
                "blitz_to_engine",
                0x00f7a64bL,
                ENGINE_FORWARD_RVA);
        expectDirectCall(
                "engine_to_entity_resolver",
                0x022f9777L,
                ENTITY_RESOLVE_RVA);
        expectInstruction(
                "entity_id_compare",
                0x022fc889L,
                "CMP dword ptr [EAX + 0x1c],ESI",
                "39701c");
        expectDirectCall(
                "resolver_to_entity_apply",
                0x022fc9abL,
                ENTITY_APPLY_RVA);

        expectInstructionSequence(
                "entity_position_anchor_setup",
                0x022fa786L,
                "8b4510568bf157f30f7e00");
        expectInstruction(
                "entity_position_anchor",
                ENTITY_POSITION_ANCHOR_RVA,
                "MOVQ XMM0,qword ptr [EAX]",
                "f30f7e00");
        expectInstruction(
                "movement_filter_member",
                0x022fa8ccL,
                "MOV ECX,dword ptr [ESI + 0x38]",
                "8b4e38");
        expectInstruction(
                "movement_filter_apply_slot",
                0x022fa917L,
                "CALL dword ptr [EAX + 0x8]",
                "ff5008");

        expectVtableSlot(
                "avatar_filter_apply_slot",
                AVATAR_FILTER_VTABLE_RVA,
                2,
                AVATAR_FILTER_APPLY_RVA);
        expectInstruction(
                "avatar_filter_helper_member",
                0x0230e8f6L,
                "MOV ECX,dword ptr [ECX + 0x8]",
                "8b4908");
        expectInstruction(
                "avatar_filter_helper_apply_slot",
                0x0230e914L,
                "CALL dword ptr [EAX + 0x8]",
                "ff5008");
        expectVtableSlot(
                "avatar_helper_store_slot",
                AVATAR_HELPER_VTABLE_RVA,
                2,
                AVATAR_HELPER_STORE_RVA);

        expectInstruction(
                "avatar_helper_ring_index",
                0x0230df7cL,
                "MOV EDX,dword ptr [ESI + 0x1c8]",
                "8b96c8010000");
        expectInstruction(
                "avatar_helper_ring_mask",
                0x0230dfa4L,
                "AND EAX,0x7",
                "83e007");
        expectInstruction(
                "avatar_helper_position_source",
                0x0230dfcaL,
                "MOV EAX,dword ptr [EBP + 0x18]",
                "8b4518");
        expectInstructionSequence(
                "avatar_helper_position_copy",
                0x0230dfd3L,
                "f30f7e00660fd6018b4008894108");
        expectInstruction(
                "avatar_helper_position_readback",
                0x0230dbe1L,
                "MOVQ XMM0,qword ptr [ESI + EDX*0x8 + 0x18]",
                "f30f7e44d618");

        String verdict = failed == 0
                ? "semantic-chain-proven"
                : "incomplete";
        String outPath = getEvidenceOutputPath(
                "type10-movement-position-trace.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("schema=wotbtreader.ghidra.type10-movement-position.v1");
        writer.println("program=" + currentProgram.getName());
        writer.println("image_base=" + imageBase);
        writer.println("executable_sha256=" + hash);
        writer.println("verdict=" + verdict);
        writer.println("checks_passed=" + passed);
        writer.println("checks_failed=" + failed);
        writer.println();
        writer.println("## semantic chain");
        writer.println("replay_dispatch_index=10");
        writer.println("type10_handler_rva=" + hex(TYPE10_HANDLER_RVA));
        writer.println("type10_payload_bytes=49");
        writer.println("type10_read_lengths=4,4,4,12,12,4,4,4,1");
        writer.println("blitz_move_rva=" + hex(BLITZ_MOVE_RVA));
        writer.println("entity_resolver_rva=" + hex(ENTITY_RESOLVE_RVA));
        writer.println("entity_apply_rva=" + hex(ENTITY_APPLY_RVA));
        writer.println("entity_position_anchor_rva=" +
                hex(ENTITY_POSITION_ANCHOR_RVA));
        writer.println("entity_position_anchor_bytes=f30f7e00");
        writer.println("entity_position_anchor_entity_register=esi");
        writer.println("entity_position_anchor_entity_id_displacement=0x1c");
        writer.println("entity_position_anchor_xyz_pointer_register=eax");
        writer.println("entity_position_anchor_xyz_displacements=0x0,0x4,0x8");
        writer.println("avatar_helper_ring_entries=8");
        writer.println("avatar_helper_ring_displacement=0x8");
        writer.println("avatar_helper_ring_stride=0x38");
        writer.println("avatar_helper_position_record_displacement=0x10");
        writer.println("avatar_helper_position_displacement=0x18");
        writer.println("avatar_helper_velocity_record_displacement=0x28");
        writer.println("avatar_helper_velocity_displacement=0x30");
        writer.println("candidate_kind=entity-bound-instruction-event");
        writer.println("stable_polling_offset_proven=false");
        writer.println("player_identity_proven=false");
        writer.println("live_capture_authorized=false");
        writer.println();
        writer.println("## checks");
        for (String check : checks) {
            writer.println(check);
        }
        writer.close();
        println("WROTE " + outPath);
        println("VERDICT " + verdict);
    }

    private void expectValue(String label, String actual, String expected) {
        record(label, expected.equalsIgnoreCase(actual),
                "expected=" + expected + " actual=" + actual);
    }

    private void expectInstruction(String label, long rva,
                                   String expectedText,
                                   String expectedHex) throws Exception {
        Instruction instruction = instructionAt(rva);
        String actualText = instruction == null ? "<missing>"
                : instruction.toString();
        String actualHex = instruction == null ? "<missing>"
                : instructionHex(instruction);
        boolean ok = instruction != null
                && expectedText.equalsIgnoreCase(actualText)
                && expectedHex.equalsIgnoreCase(actualHex);
        record(label, ok, "rva=" + hex(rva) + " expected_text=" +
                expectedText + " actual_text=" + actualText +
                " expected_hex=" + expectedHex + " actual_hex=" +
                actualHex);
    }

    private void expectInstructionContains(String label, long rva,
                                           String expectedText)
            throws Exception {
        Instruction instruction = instructionAt(rva);
        String actualText = instruction == null ? "<missing>"
                : instruction.toString();
        boolean ok = instruction != null && actualText.toUpperCase(Locale.ROOT)
                .contains(expectedText.toUpperCase(Locale.ROOT));
        record(label, ok, "rva=" + hex(rva) + " expected_contains=" +
                expectedText + " actual_text=" + actualText);
    }

    private void expectInstructionSequence(String label, long rva,
                                           String expectedHex)
            throws Exception {
        Address start = address(rva);
        byte[] bytes = new byte[expectedHex.length() / 2];
        int read = currentProgram.getMemory().getBytes(start, bytes);
        String actualHex = read == bytes.length ? toHex(bytes) : "<partial>";
        record(label, expectedHex.equalsIgnoreCase(actualHex),
                "rva=" + hex(rva) + " expected_hex=" + expectedHex +
                " actual_hex=" + actualHex);
    }

    private void expectDirectCall(String label, long callRva,
                                  long targetRva) throws Exception {
        Instruction instruction = instructionAt(callRva);
        boolean ok = instruction != null
                && "CALL".equalsIgnoreCase(instruction.getMnemonicString());
        if (ok) {
            ok = false;
            for (Address flow : instruction.getFlows()) {
                if (flow.equals(address(targetRva))) {
                    ok = true;
                    break;
                }
            }
        }
        record(label, ok, "call_rva=" + hex(callRva) + " target_rva=" +
                hex(targetRva) + " actual=" +
                (instruction == null ? "<missing>" : instruction.toString()));
    }

    private void expectVtableSlot(String label, long tableRva, int slot,
                                  long targetRva) throws Exception {
        Address slotAddress = address(tableRva + (long)slot * 4L);
        long actual = Integer.toUnsignedLong(
                currentProgram.getMemory().getInt(slotAddress));
        long expected = address(targetRva).getOffset();
        record(label, actual == expected,
                "table_rva=" + hex(tableRva) + " slot=" + slot +
                " expected_target_rva=" + hex(targetRva) +
                " actual_target_rva=" +
                hex(actual - currentProgram.getImageBase().getOffset()));
    }

    private Instruction instructionAt(long rva) {
        Listing listing = currentProgram.getListing();
        return listing.getInstructionAt(address(rva));
    }

    private Address address(long rva) {
        return currentProgram.getImageBase().add(rva);
    }

    private String instructionHex(Instruction instruction) throws Exception {
        byte[] bytes = new byte[instruction.getLength()];
        int read = currentProgram.getMemory().getBytes(
                instruction.getAddress(), bytes);
        return read == bytes.length ? toHex(bytes) : "<partial>";
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
        return "0x" + Long.toHexString(value);
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
