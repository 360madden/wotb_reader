// TraceEntityRegistryPosition.java - verify the current-build module-rooted
// path from the published GameCore pointer through AppController,
// SessionController, and the active PlaybackController to its replay-owned
// BWEntities and the verified movement-filter-helper position ring.
//
// This script is hash-bound static evidence only. It does not authorize a
// live process read and it does not promote the layout into memory-offsets.
// Run it only against the already analyzed, hash-verified Ghidra project.
//
// Important headless rule: Ghidra can return exit zero after a script compile
// or runtime error. Callers must also reject SCRIPT ERROR/error: in the script
// log, require a fresh report, and require
// verdict=replay-resolver-layout-proven.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;

public class TraceEntityRegistryPosition extends GhidraScript {

    private static final String EXPECTED_SHA256 =
            "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    private static final long GAME_CORE_ROOT_RVA = 0x04095c88L;
    private static final long GAME_CORE_ROOT_STORE_RVA = 0x00280360L;
    private static final long APP_CONTROLLER_CONSTRUCTOR_RVA = 0x00e7fc70L;
    private static final long APP_CONTROLLER_VTABLE_RVA = 0x0323d61cL;
    private static final long SESSION_CONTROLLER_CONSTRUCTOR_RVA = 0x00e855f0L;
    private static final long SESSION_CONTROLLER_VTABLE_RVA = 0x0323d9bcL;
    private static final long ACCOUNT_CONTROLLER_CONSTRUCTOR_RVA = 0x00e7a970L;
    private static final long PLAYBACK_CONTROLLER_CONSTRUCTOR_RVA = 0x00fbaa40L;
    private static final long ACCOUNT_CONTROLLER_VTABLE_RVA = 0x0323eae4L;
    private static final long PLAYBACK_CONTROLLER_VTABLE_RVA = 0x03253aa4L;
    private static final long BW_CONNECTION_CONSTRUCTOR_RVA = 0x022f8ef0L;
    private static final long BW_CONNECTION_BASE_CONSTRUCTOR_RVA = 0x022fd420L;
    private static final long BW_ENTITIES_CONSTRUCTOR_RVA = 0x022fb880L;
    private static final long ENTITY_TREE_FIND_RVA = 0x002cb980L;
    private static final long ENTITY_RESOLVER_RVA = 0x022fc850L;
    private static final long ENTITY_APPLY_RVA = 0x022fa780L;
    private static final long KINETICS_FILTER_VTABLE_RVA = 0x0325654cL;
    private static final long VEHICLE_FILTER_VTABLE_RVA = 0x032565acL;
    private static final long AVATAR_FILTER_VTABLE_RVA = 0x03442520L;
    private static final long AVATAR_FILTER_APPLY_RVA = 0x0230e8f0L;
    private static final long KINETICS_HELPER_VTABLE_RVA = 0x0325656cL;
    private static final long VEHICLE_HELPER_VTABLE_RVA = 0x0325658cL;
    private static final long AVATAR_HELPER_VTABLE_RVA = 0x034424a4L;
    private static final long VEHICLE_FILTER_CONSTRUCTOR_RVA = 0x01013930L;
    private static final long VEHICLE_HELPER_CONSTRUCTOR_RVA = 0x010139b0L;
    private static final long BASE_HELPER_CONSTRUCTOR_RVA = 0x0230d000L;
    private static final long VEHICLE_HELPER_FACTORY_RVA = 0x01069be0L;
    private static final long VEHICLE_HELPER_STORE_RVA = 0x01069c80L;
    private static final long AVATAR_HELPER_STORE_RVA = 0x0230df40L;
    private static final long AVATAR_HELPER_READBACK_RVA = 0x0230dba0L;

    private final List<String> checks = new ArrayList<String>();
    private int passed;
    private int failed;

    @Override
    public void run() throws Exception {
        String hash = executableHash();
        expectValue("program_name", currentProgram.getName(), "wotblitz.exe");
        expectValue("executable_sha256", hash, EXPECTED_SHA256);

        MemoryBlock rootBlock = currentProgram.getMemory().getBlock(
                address(GAME_CORE_ROOT_RVA));
        record("game_core_root_non_executable",
                rootBlock != null && !rootBlock.isExecute(),
                "root_rva=" + hex(GAME_CORE_ROOT_RVA) + " block=" +
                (rootBlock == null ? "<missing>" : rootBlock.getName()));
        expectInstruction(
                "game_core_root_store",
                GAME_CORE_ROOT_STORE_RVA,
                "MOV dword ptr [0x04495c88],EDI",
                "893d885c4904");
        expectDataReference(
                "game_core_root_store_reference",
                GAME_CORE_ROOT_STORE_RVA,
                GAME_CORE_ROOT_RVA);
        expectInstruction(
                "game_core_root_runtime_read",
                0x00282f23L,
                "MOV ECX,dword ptr [0x04495c88]",
                "8b0d885c4904");
        expectDataReference(
                "game_core_root_runtime_read_reference",
                0x00282f23L,
                GAME_CORE_ROOT_RVA);

        expectDirectCall(
                "game_core_constructs_app_controller",
                0x00280eebL,
                APP_CONTROLLER_CONSTRUCTOR_RVA);
        expectInstruction(
                "game_core_app_controller_member",
                0x00280f27L,
                "MOV dword ptr [EBX + 0xc],EDI",
                "897b0c");
        expectInstruction(
                "app_controller_vtable",
                0x00e7fcdfL,
                "MOV dword ptr [EDI],0x363d61c",
                "c7071cd66303");
        expectVtableSlot(
                "app_controller_session_start_slot",
                APP_CONTROLLER_VTABLE_RVA,
                5,
                0x00e95940L);
        expectDataReference(
                "app_controller_registers_session_builder",
                0x00e95c02L,
                0x00eb17f0L);
        expectDirectCall(
                "app_controller_constructs_session_controller",
                0x00eb18c1L,
                SESSION_CONTROLLER_CONSTRUCTOR_RVA);
        expectInstruction(
                "app_controller_session_controller_member",
                0x00eb18f9L,
                "MOV dword ptr [EDX + 0x124],EAX",
                "898224010000");

        expectInstruction(
                "session_controller_vtable",
                0x00e8564dL,
                "MOV dword ptr [EBX],0x363d9bc",
                "c703bcd96303");
        expectVtableSlot(
                "session_controller_account_start_slot",
                SESSION_CONTROLLER_VTABLE_RVA,
                5,
                0x00e96e50L);
        expectDataReference(
                "session_controller_registers_account_builder",
                0x00e96efbL,
                0x00eacf30L);
        expectDirectCall(
                "session_controller_constructs_account_controller",
                0x00ead0ebL,
                ACCOUNT_CONTROLLER_CONSTRUCTOR_RVA);
        expectInstruction(
                "session_controller_account_controller_member",
                0x00ead18fL,
                "MOV dword ptr [EDI + 0x118],ESI",
                "89b718010000");

        expectInstruction(
                "account_controller_vtable",
                0x00e7a9f4L,
                "MOV dword ptr [EBX],0x363eae4",
                "c703e4ea6303");
        expectDirectCall(
                "start_replay_constructs_playback_controller",
                0x00ecd0c2L,
                PLAYBACK_CONTROLLER_CONSTRUCTOR_RVA);
        expectInstruction(
                "account_controller_active_playback_member",
                0x00ecd13dL,
                "MOV dword ptr [EBX + 0x128],ECX",
                "898b28010000");

        expectInstruction(
                "playback_controller_vtable",
                0x00fbaa96L,
                "MOV dword ptr [EDI],0x3653aa4",
                "c707a43a6503");
        expectDirectCall(
                "playback_controller_constructs_connection",
                0x00fbac3fL,
                BW_CONNECTION_CONSTRUCTOR_RVA);
        expectInstruction(
                "playback_controller_connection_member",
                0x00fbacabL,
                "MOV dword ptr [EDI + 0x120],ESI",
                "89b720010000");
        expectInstruction(
                "playback_handler_uses_connection",
                0x00fbaef9L,
                "MOV ESI,dword ptr [EDI + 0x120]",
                "8bb720010000");
        expectInstruction(
                "playback_handler_uses_embedded_entities",
                0x00fbaf05L,
                "ADD ESI,0x4",
                "83c604");
        expectDirectCall(
                "playback_constructs_handler_from_entities",
                0x00fbaf2dL,
                0x00f603d0L);

        expectDirectCall(
                "connection_to_base_constructor",
                0x022f8f22L,
                BW_CONNECTION_BASE_CONSTRUCTOR_RVA);
        expectInstruction(
                "connection_entities_subobject",
                0x022fd44dL,
                "LEA EDI,[ESI + 0x4]",
                "8d7e04");
        expectDirectCall(
                "connection_constructs_entities",
                0x022fd459L,
                BW_ENTITIES_CONSTRUCTOR_RVA);

        expectInstructionContains(
                "entities_primary_map_member",
                0x022fb8acL,
                "LEA ECX,[ESI + 0xc]");
        expectInstructionContains(
                "entities_secondary_map_member",
                0x022fb8cbL,
                "LEA ECX,[ESI + 0x24]");
        expectInstructionContains(
                "entities_tertiary_map_member",
                0x022fb8d8L,
                "LEA ECX,[ESI + 0x3c]");
        expectInstruction(
                "entities_cached_entity_member",
                0x022fb8ebL,
                "MOV dword ptr [EAX],0x0",
                "c70000000000");

        expectInstruction(
                "resolver_cached_entity_member",
                0x022fc880L,
                "MOV EAX,dword ptr [EBX + 0x48]",
                "8b4348");
        expectInstruction(
                "resolver_cached_entity_id",
                0x022fc889L,
                "CMP dword ptr [EAX + 0x1c],ESI",
                "39701c");
        expectInstructionContains(
                "resolver_primary_map",
                0x022fc88eL,
                "LEA ECX,[EBX + 0xc]");
        expectDirectCall(
                "resolver_primary_lookup",
                0x022fc892L,
                0x022fe620L);
        expectInstructionContains(
                "resolver_tertiary_map",
                0x022fc89bL,
                "LEA ECX,[EBX + 0x3c]");
        expectDirectCall(
                "resolver_tertiary_lookup",
                0x022fc89fL,
                0x02300a20L);
        expectInstructionContains(
                "resolver_secondary_map",
                0x022fc8a8L,
                "LEA ECX,[EBX + 0x24]");
        expectDirectCall(
                "resolver_secondary_lookup",
                0x022fc8acL,
                0x022ffb10L);
        expectDirectCall(
                "resolver_to_entity_apply",
                0x022fc9abL,
                ENTITY_APPLY_RVA);

        expectInstructionContains(
                "primary_tree_object_adjustment",
                0x022fe62bL,
                "LEA ESI,[ECX + 0x10]");
        expectInstructionContains(
                "tertiary_tree_object_adjustment",
                0x02300a2bL,
                "LEA ESI,[ECX + 0x4]");
        expectInstructionContains(
                "secondary_tree_object_adjustment",
                0x022ffb1bL,
                "LEA ESI,[ECX + 0x10]");
        expectInstructionContains(
                "primary_tree_value_member",
                0x022fe657L,
                "MOV EAX,dword ptr [ECX + 0x14]");
        expectInstructionContains(
                "tertiary_tree_value_member",
                0x02300a5fL,
                "MOV EAX,dword ptr [ECX + 0x14]");
        expectInstructionContains(
                "secondary_tree_value_member",
                0x022ffb4fL,
                "MOV EAX,dword ptr [ECX + 0x14]");
        expectInstruction(
                "tree_object_sentinel",
                ENTITY_TREE_FIND_RVA + 0x3L,
                "MOV ECX,dword ptr [ECX]",
                "8b09");
        expectInstruction(
                "tree_sentinel_root",
                ENTITY_TREE_FIND_RVA + 0x8L,
                "MOV EAX,dword ptr [ECX + 0x4]",
                "8b4104");
        expectInstructionContains(
                "tree_node_nil_flag",
                ENTITY_TREE_FIND_RVA + 0x17L,
                "CMP byte ptr [EAX + 0xd],0x0");
        expectInstructionContains(
                "tree_node_signed_key_compare",
                ENTITY_TREE_FIND_RVA + 0x25L,
                "CMP dword ptr [EAX + 0x10],ESI");
        expectInstruction(
                "tree_node_right_child",
                ENTITY_TREE_FIND_RVA + 0x2aL,
                "MOV EAX,dword ptr [EAX + 0x8]",
                "8b4008");
        expectInstruction(
                "tree_node_left_child",
                ENTITY_TREE_FIND_RVA + 0x39L,
                "MOV EAX,dword ptr [EAX]",
                "8b00");

        expectInstruction(
                "entity_movement_filter_member",
                0x022fa8ccL,
                "MOV ECX,dword ptr [ESI + 0x38]",
                "8b4e38");
        expectVtableSlot(
                "kinetics_filter_apply_slot",
                KINETICS_FILTER_VTABLE_RVA,
                2,
                AVATAR_FILTER_APPLY_RVA);
        expectVtableSlot(
                "vehicle_filter_apply_slot",
                VEHICLE_FILTER_VTABLE_RVA,
                2,
                AVATAR_FILTER_APPLY_RVA);
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
        expectVtableSlot(
                "kinetics_helper_store_slot",
                KINETICS_HELPER_VTABLE_RVA,
                2,
                AVATAR_HELPER_STORE_RVA);
        expectVtableSlot(
                "avatar_helper_store_slot",
                AVATAR_HELPER_VTABLE_RVA,
                2,
                AVATAR_HELPER_STORE_RVA);
        expectInstruction(
                "vehicle_filter_constructor_vtable",
                VEHICLE_FILTER_CONSTRUCTOR_RVA + 0x4dL,
                "MOV dword ptr [ESI],0x36565ac",
                "c706ac656503");
        expectVtableSlot(
                "vehicle_filter_helper_factory_slot",
                VEHICLE_FILTER_VTABLE_RVA,
                6,
                VEHICLE_HELPER_FACTORY_RVA);
        expectDirectCall(
                "vehicle_filter_factory_constructs_helper",
                VEHICLE_HELPER_FACTORY_RVA + 0x54L,
                VEHICLE_HELPER_CONSTRUCTOR_RVA);
        expectInstruction(
                "vehicle_filter_factory_assigns_helper",
                VEHICLE_HELPER_FACTORY_RVA + 0x60L,
                "MOV dword ptr [ESI + 0x8],EAX",
                "894608");
        expectDirectCall(
                "vehicle_helper_constructs_base_helper",
                VEHICLE_HELPER_CONSTRUCTOR_RVA + 0x33L,
                BASE_HELPER_CONSTRUCTOR_RVA);
        expectInstruction(
                "vehicle_helper_vtable",
                VEHICLE_HELPER_CONSTRUCTOR_RVA + 0x41L,
                "MOV dword ptr [EBX],0x365658c",
                "c7038c656503");
        expectInstruction(
                "base_helper_ring_entry_count",
                BASE_HELPER_CONSTRUCTOR_RVA + 0x3aL,
                "MOV EDI,0x8",
                "bf08000000");
        expectInstruction(
                "base_helper_position_member_cursor",
                BASE_HELPER_CONSTRUCTOR_RVA + 0x45L,
                "LEA ESI,[EBX + 0x24]",
                "8d7324");
        expectInstruction(
                "base_helper_first_position_member",
                BASE_HELPER_CONSTRUCTOR_RVA + 0x48L,
                "LEA ECX,[ESI + -0xc]",
                "8d4ef4");
        expectInstruction(
                "base_helper_position_member_stride",
                BASE_HELPER_CONSTRUCTOR_RVA + 0x57L,
                "ADD ESI,0x38",
                "83c638");
        expectVtableSlot(
                "vehicle_helper_store_slot",
                VEHICLE_HELPER_VTABLE_RVA,
                2,
                VEHICLE_HELPER_STORE_RVA);
        expectDirectCall(
                "vehicle_helper_calls_common_store",
                VEHICLE_HELPER_STORE_RVA + 0x14dL,
                AVATAR_HELPER_STORE_RVA);
        expectVtableSlot(
                "vehicle_helper_readback_slot",
                VEHICLE_HELPER_VTABLE_RVA,
                4,
                AVATAR_HELPER_READBACK_RVA);
        expectInstruction(
                "avatar_helper_current_index",
                0x0230df7cL,
                "MOV EDX,dword ptr [ESI + 0x1c8]",
                "8b96c8010000");
        expectInstruction(
                "avatar_helper_current_record_time",
                0x0230df95L,
                "MOVSD XMM0,qword ptr [ESI + EAX*0x8 + 0x8]",
                "f20f1044c608");
        expectInstruction(
                "avatar_helper_index_store",
                0x0230dfaaL,
                "MOV dword ptr [ESI + 0x1c8],EAX",
                "8986c8010000");
        expectInstructionSequence(
                "avatar_helper_position_copy",
                0x0230dfd3L,
                "f30f7e00660fd6018b4008894108");
        expectInstructionSequence(
                "avatar_helper_velocity_copy",
                0x0230dff8L,
                "f30f7e00660fd644d6308b40088944d638");
        expectInstruction(
                "avatar_helper_position_readback",
                0x0230dbe1L,
                "MOVQ XMM0,qword ptr [ESI + EDX*0x8 + 0x18]",
                "f30f7e44d618");

        String verdict = failed == 0
                ? "replay-resolver-layout-proven"
                : "incomplete";
        String outPath = getEvidenceOutputPath(
                "entity-registry-position-trace.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("schema=wotbtreader.ghidra.entity-registry-position.v1");
        writer.println("program=" + currentProgram.getName());
        writer.println("executable_sha256=" + hash);
        writer.println("verdict=" + verdict);
        writer.println("checks_passed=" + passed);
        writer.println("checks_failed=" + failed);
        writer.println();
        writer.println("## resolver layout");
        writer.println("game_core_root_rva=" + hex(GAME_CORE_ROOT_RVA));
        writer.println("game_core_app_controller_displacement=0xc");
        writer.println("app_controller_vtable_rva=" +
                hex(APP_CONTROLLER_VTABLE_RVA));
        writer.println("app_controller_session_controller_displacement=0x124");
        writer.println("session_controller_vtable_rva=" +
                hex(SESSION_CONTROLLER_VTABLE_RVA));
        writer.println("session_controller_account_controller_displacement=0x118");
        writer.println("account_controller_vtable_rva=" +
                hex(ACCOUNT_CONTROLLER_VTABLE_RVA));
        writer.println("account_controller_active_controller_displacement=0x128");
        writer.println("playback_controller_vtable_rva=" +
                hex(PLAYBACK_CONTROLLER_VTABLE_RVA));
        writer.println("playback_controller_connection_displacement=0x120");
        writer.println("connection_entities_displacement=0x4");
        writer.println("entities_cached_entity_displacement=0x48");
        writer.println("entities_tree_object_displacements=0x1c,0x40,0x34");
        writer.println("tree_node_left_displacement=0x0");
        writer.println("tree_node_parent_displacement=0x4");
        writer.println("tree_node_right_displacement=0x8");
        writer.println("tree_node_nil_displacement=0xd");
        writer.println("tree_node_key_displacement=0x10");
        writer.println("tree_node_value_displacement=0x14");
        writer.println("entity_id_displacement=0x1c");
        writer.println("entity_movement_filter_displacement=0x38");
        writer.println("movement_filter_vtable_rvas=0x325654c,0x32565ac,0x3442520");
        writer.println("avatar_filter_helper_displacement=0x8");
        writer.println("avatar_helper_vtable_rvas=0x325656c,0x325658c,0x34424a4");
        writer.println("vehicle_helper_factory_rva=" +
                hex(VEHICLE_HELPER_FACTORY_RVA));
        writer.println("vehicle_helper_constructor_rva=" +
                hex(VEHICLE_HELPER_CONSTRUCTOR_RVA));
        writer.println("vehicle_helper_store_wrapper_rva=" +
                hex(VEHICLE_HELPER_STORE_RVA));
        writer.println("avatar_helper_current_index_displacement=0x1c8");
        writer.println("avatar_helper_ring_displacement=0x8");
        writer.println("avatar_helper_ring_stride=0x38");
        writer.println("avatar_helper_ring_entries=8");
        writer.println("position_record_displacement=0x10");
        writer.println("position_helper_displacement=0x18");
        writer.println("velocity_record_displacement=0x28");
        writer.println("velocity_helper_displacement=0x30");
        writer.println("resolver_kind=module-rooted-active-replay-entity-id-map");
        writer.println("live_read_proven=false");
        writer.println("hardware_atomic_read_proven=false");
        writer.println("same_decoded_clock_proven=false");
        writer.println("offset_table_promotion_ready=false");
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
                                           String expectedText) {
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
                                  long targetRva) {
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

    private void expectDataReference(String label, long instructionRva,
                                     long targetRva) {
        Instruction instruction = instructionAt(instructionRva);
        boolean ok = false;
        if (instruction != null) {
            Reference[] references = instruction.getReferencesFrom();
            for (Reference reference : references) {
                if (reference.getToAddress().equals(address(targetRva))) {
                    ok = true;
                    break;
                }
            }
        }
        record(label, ok, "instruction_rva=" + hex(instructionRva) +
                " target_rva=" + hex(targetRva));
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
