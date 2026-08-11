// VerifyTransformRecord.java - hash-bound static verifier for the entity
// transform record discovery chain (OD-RECOVERY-053 / FRESH43, 11.19.0.10):
//
//   VehicleGameLogic entity (this) -> [entity+0x3C] via getter FUN_00d29ea0
//   -> transform object with:
//        position float32 triple  [transform+0x1C / +0x20 / +0x24]
//        quaternion source        [transform+0x10 .. +0x1C]  (FUN_00d1a0f0)
//        world matrix 4x4         [transform+0x60 .. +0x9C]  (4x MOVUPS)
//        rotation region          [transform+0x38 .. +0x58]
//
//   The per-frame fill is FUN_00bc3940 (RVA 0x7c3940), called from the
//   entity list FUN_00bb9b30 when [entity+0x20] & 0x800 is set. It gates on
//   a non-zero position triple, composes the world matrix via the matrix
//   multiply FUN_00729570 (quaternion->matrix FUN_00d1a0f0 + basis
//   normalizer FUN_00d155c0), then stores rotation.
//
// Hash-bound static evidence only; no live read, no promotion.
import java.io.File;
import java.io.PrintWriter;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;

public class VerifyTransformRecord extends GhidraScript {

    private int pass = 0;
    private int fail = 0;
    private final StringBuilder out = new StringBuilder();

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        long base = imageBase.getOffset();
        out.append("## VerifyTransformRecord — entity transform record\n\n");
        out.append("program=" + currentProgram.getName() + "\n");
        out.append("executable_sha256=" + currentProgram.getExecutableSHA256()
                + "\n\n");

        // ---- 1. transform getter FUN_00d29ea0 = return [ECX+0x3C] ----
        out.append("### 1. transform getter FUN_00d29ea0 (RVA 0x929ea0)\n");
        byte[] getterBytes = readBytes(base + 0x929ea0L, 6);
        String getterHex = bytesToHex(getterBytes);
        check("getter_bytes_8b413cc20400",
                getterHex.equals("8b413cc20400"), "bytes=" + getterHex);

        // ---- 2. fill site FUN_00bc3940 calls the getter ----
        out.append("### 2. fill site FUN_00bc3940 (RVA 0x7c3940): calls getter\n");
        String dis = disasmRange(0x7c3940, 0x7c3940 + 0x300);
        check("fill_calls_getter_d29ea0",
                dis.contains("CALL 0x00d29ea0"), "");

        // ---- 3. position triple reads [t+0x1c/0x20/0x24] ----
        out.append("### 3. position triple via [EDX+0xc/0x10/0x14], EDX=[t+0x10]\n");
        check("fill_reads_pos_x_1c", dis.contains("MOVSS XMM0,dword ptr [EDX + 0xc]"), "");
        check("fill_reads_pos_y_20", dis.contains("MOVSS XMM0,dword ptr [EDX + 0x10]"), "");
        check("fill_reads_pos_z_24", dis.contains("MOVSS XMM0,dword ptr [EDX + 0x14]"), "");

        // ---- 4. world matrix target [t+0x60] ----
        out.append("### 4. matrix target [t+0x60] (ADD EAX,0x60 after getter)\n");
        check("fill_matrix_target_60", dis.contains("ADD EAX,0x60"), "");

        // ---- 5. 4x MOVUPS matrix store to [t+0x60..0x9c] ----
        out.append("### 5. world matrix 4x4 store (4x MOVUPS from +0x60)\n");
        check("matrix_store_row0", dis.contains("MOVUPS xmmword ptr [ESI],XMM0"), "");
        check("matrix_store_row1", dis.contains("MOVUPS xmmword ptr [ESI + 0x10],XMM0"), "");
        check("matrix_store_row2", dis.contains("MOVUPS xmmword ptr [ESI + 0x20],XMM0"), "");
        check("matrix_store_row3", dis.contains("MOVUPS xmmword ptr [ESI + 0x30],XMM0"), "");

        // ---- 6. rotation region writes [t+0x38..0x58] ----
        out.append("### 6. rotation region [t+0x38..0x58]\n");
        check("rotation_write_38", dis.contains("MOVQ qword ptr [EBX + 0x38],XMM0"), "");
        check("rotation_write_40", dis.contains("MOV dword ptr [EBX + 0x40],EAX"), "");
        check("rotation_write_44", dis.contains("MOVQ qword ptr [EBX + 0x44],XMM0"), "");
        check("rotation_write_4c", dis.contains("MOV dword ptr [EBX + 0x4c],EAX"), "");
        check("rotation_write_50", dis.contains("MOVUPS xmmword ptr [EBX + 0x50],XMM0"), "");

        // ---- 7. quaternion pipeline ----
        out.append("### 7. quaternion->matrix + matrix multiply + normalizer\n");
        check("fill_calls_quat_to_matrix_d1a0f0",
                dis.contains("CALL 0x00d1a0f0"), "");
        check("fill_calls_matrix_multiply_729570",
                dis.contains("CALL 0x00729570"), "");
        check("fill_calls_basis_normalizer_d155c0",
                dis.contains("CALL 0x00d155c0"), "");

        // ---- 8. entity-list caller gates on [entity+0x20] & 0x800 ----
        out.append("### 8. entity-list caller FUN_00bb9b30 (RVA 0x7b9b30)\n");
        String disCaller = disasmRange(0x7b9b30, 0x7b9b30 + 0x120);
        check("caller_gates_0x800", disCaller.contains("TEST EAX,0x800"), "");
        check("caller_calls_fill_bc3940",
                disCaller.contains("CALL 0x00bc3940"), "");

        // ---- summary ----
        out.append("\nPASS=" + pass + " FAIL=" + fail + "\n");
        String verdict = fail == 0 ? "transform-record-verified"
                : "transform-record-failed";
        out.append("VERDICT " + verdict + "\n");

        String outPath = getEvidenceOutputPath("verify-transform-record.txt");
        PrintWriter w = new PrintWriter(new File(outPath));
        w.print(out);
        w.close();
        println("VERDICT pass=" + pass + " fail=" + fail + " "
                + verdict);
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
