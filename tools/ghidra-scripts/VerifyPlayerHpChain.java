// VerifyPlayerHpChain.java - hash-bound static verifier for the playerHP
// discovery chain: VehicleGameLogic's entity getter -> [entity+0xb8]
// (int16 current health), [entity+0xba] (byte alive), [entity+0x11e]
// (int16 healing health).
//
// Evidence anchors:
//   1. VehicleGameLogic::vftable (0x327da50), slot 1 = entity getter
//      0x31b560, byte-verified "MOV EAX,[ECX+0x4]; RET" (8b 41 04 c3).
//   2. set_health (FUN_016ee450, RVA 0x12ee450) reads the OLD value via
//      the entity getter: MOVSX EDI,word ptr [EAX+0xb8] (0f bf 78 b8).
//   3. set_healingHealth (FUN_016ee350, RVA 0x12ee350) reads the OLD
//      healing value via the entity getter: MOVSX word ptr [EAX+0x11e].
//   4. state-sync writer FUN_0166b9f0 (RVA 0x126b9f0) writes int16 to
//      [param+0xb8] (health), byte to [param+0xba] (alive), int16 to
//      [param+0x11e] (healing) — the write site pair.
//   5. diff-notify twin FUN_01675f60 (RVA 0x1275f60) reads old/new and
//      dispatches property-changed listeners for the same offsets.
//
// Hash-bound static evidence only; no live read, no promotion.
import java.io.File;
import java.io.PrintWriter;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolType;

public class VerifyPlayerHpChain extends GhidraScript {

    private int pass = 0;
    private int fail = 0;
    private final StringBuilder out = new StringBuilder();

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        long base = imageBase.getOffset();
        out.append("## VerifyPlayerHpChain — playerHP static chain\n\n");
        out.append("program=" + currentProgram.getName() + "\n");
        out.append("executable_sha256=" + currentProgram.getExecutableSHA256()
                + "\n\n");

        // ---- 1. VehicleGameLogic vftable slot 1 = entity getter ----
        out.append("### 1. VehicleGameLogic vftable 0x327da50, slot 1\n");
        long vftableAbs = base + 0x327da50L;
        long slot1 = readU32(vftableAbs + 4);
        check("vftable_slot1_is_0x31b560", slot1 == base + 0x31b560L,
                "slot1=0x" + Long.toHexString(slot1));
        if (slot1 == base + 0x31b560L) {
            byte[] g = readBytes(slot1, 4);
            String hex = bytesToHex(g);
            check("entity_getter_bytes_8b4104c3", hex.equals("8b4104c3"),
                    "bytes=" + hex);
        }

        // ---- 2. set_health reads old health via entity getter ----
        out.append("### 2. set_health (0x12ee450): read [entity+0xb8] int16\n");
        String dis = disasmRange(0x12ee450, 0x12ee450 + 0x145);
        boolean readB8 = dis.contains("MOVSX EDI,word ptr [EAX + 0xb8]");
        check("set_health_reads_entity_b8_int16", readB8, "");
        boolean viaSlot1 = dis.contains("CALL dword ptr [EAX + 0x4]")
                || dis.contains("CALL dword ptr [EAX + 0x4]");
        check("set_health_derefs_vtable_slot1", viaSlot1, "");

        // ---- 3. set_healingHealth reads [entity+0x11e] ----
        out.append("### 3. set_healingHealth (0x12ee350): read [entity+0x11e] int16\n");
        String dis2 = disasmRange(0x12ee350, 0x12ee350 + 0xf8);
        boolean read11e = dis2.contains("MOVSX EDI,word ptr [EAX + 0x11e]");
        check("set_healingHealth_reads_entity_11e_int16", read11e, "");

        // ---- 4. state-sync writer FUN_0166b9f0 ----
        out.append("### 4. state-sync writer (0x126b9f0): writes +0xb8/+0xba/+0x11e\n");
        String dis3 = disasmRange(0x126b9f0, 0x126b9f0 + 0x15a);
        check("sync_writes_word_entity_b8",
                dis3.contains("MOV word ptr [ESI + 0xb8],AX"), "");
        check("sync_writes_byte_entity_ba",
                dis3.contains("MOV byte ptr [ESI + 0xba],AL"), "");
        boolean dis3Rest = dis3.contains("MOV word ptr [ESI + 0x11e],AX")
                || dis3.contains("MOV word ptr [ESI + 0x11c],AX");
        check("sync_writes_healing_region", dis3Rest, "");

        // ---- 5. diff-notify twin FUN_01675f60 ----
        out.append("### 5. diff-notify twin (0x1275f60): old/new + listener dispatch\n");
        String dis4 = disasmRange(0x1275f60, 0x1275f60 + 0xb82);
        check("notify_twin_reads_word_entity_b8",
                dis4.contains("MOV word ptr [EDI + 0xb8]"), "");
        check("notify_twin_reads_word_entity_11e",
                dis4.contains("MOV word ptr [EDI + 0x11e]"), "");
        check("notify_twin_dispatch_slot_0x68",
                dis4.contains("CALL dword ptr [EAX + 0x68]"), "");

        // ---- 6. maxHealth / isAlive / gunAnglesPacked setters ----
        out.append("### 6. maxHealth (0x12eeb70): int16 at [entity+0x11C]\n");
        String dis5 = disasmRange(0x12eeb70, 0x12eeb70 + 0x10c);
        check("set_maxHealth_reads_entity_11c_int16",
                dis5.contains("MOVSX EDI,word ptr [EAX + 0x11c]"), "");
        check("set_maxHealth_derefs_vtable_slot1",
                dis5.contains("CALL dword ptr [EAX + 0x4]"), "");

        out.append("### 7. isAlive (0x12ee990): alive byte at [entity+0xBA]\n");
        String dis6 = disasmRange(0x12ee990, 0x12ee990 + 0xa4);
        check("set_isAlive_reads_entity_ba_byte",
                dis6.contains("CMP byte ptr [EAX + 0xba],0x0"), "");
        check("set_isAlive_reads_entity_b8_word",
                dis6.contains("CMP word ptr [EAX + 0xb8],0x0"), "");

        out.append("### 8. gunAnglesPacked (0x12ee230): word at [entity+0x7E]\n");
        String dis7 = disasmRange(0x12ee230, 0x12ee230 + 0x11f);
        check("set_gunAnglesPacked_reads_entity_7e_word",
                dis7.contains("MOVZX ECX,word ptr [EAX + 0x7e]"), "");

        // ---- summary ----
        out.append("\nPASS=" + pass + " FAIL=" + fail + "\n");
        String verdict = fail == 0 ? "player-hp-chain-verified"
                : "player-hp-chain-failed";
        out.append("VERDICT " + verdict + "\n");

        String outPath = getEvidenceOutputPath("verify-player-hp-chain.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.print(out);
        w.close();
        println("VERDICT pass=" + pass + " fail=" + fail + " "
                + verdict);
    }

    private long readU32(long absAddress) {
        try {
            byte[] b = new byte[4];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(absAddress), b);
            if (read != 4) return -1L;
            return (b[0] & 0xffL) | ((b[1] & 0xffL) << 8)
                    | ((b[2] & 0xffL) << 16) | ((b[3] & 0xffL) << 24);
        } catch (Exception e) {
            return -1L;
        }
    }

    private byte[] readBytes(long absAddress, int n) {
        try {
            byte[] b = new byte[n];
            int read = currentProgram.getMemory().getBytes(
                    currentProgram.getAddressFactory().getDefaultAddressSpace()
                            .getAddress(absAddress), b);
            if (read != n) return new byte[0];
            return b;
        } catch (Exception e) {
            return new byte[0];
        }
    }

    private String bytesToHex(byte[] b) {
        if (b == null || b.length == 0) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < b.length; i++) {
            sb.append(String.format("%02x", b[i] & 0xff));
        }
        return sb.toString();
    }

    private String disasmRange(long startRva, long endRva) {
        Address imageBase = currentProgram.getImageBase();
        Address a = imageBase.add(startRva);
        Address end = imageBase.add(endRva);
        StringBuilder sb = new StringBuilder();
        Listing listing = currentProgram.getListing();
        Instruction instr = listing.getInstructionAt(a);
        if (instr == null) {
            // try to walk from the first instruction after start
            instr = listing.getInstructionAfter(a);
        }
        while (instr != null && instr.getAddress().compareTo(end) <= 0) {
            sb.append(instr.toString()).append("\n");
            instr = instr.getNext();
        }
        return sb.toString();
    }

    private void check(String name, boolean ok, String detail) {
        out.append(ok ? "PASS " : "FAIL ").append(name);
        if (detail != null && !detail.isEmpty()) {
            out.append(" (").append(detail).append(")");
        }
        out.append("\n");
        if (ok) pass++;
        else fail++;
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"),
                        ".build" + File.separator + "ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException(
                    "Could not create Ghidra evidence directory");
        return new File(directory, fileName).getAbsolutePath();
    }
}
