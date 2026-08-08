// FindVehiclePositionFamily.java - current-build structural triage for the
// community VehicleGameLogic -> Vehicle -> position candidate family.
//
// Historical community layout (candidate only):
//   VehicleGameLogic + 0x04 -> Vehicle
//   Vehicle + 0x68 / 0x6C / 0x70 -> position XYZ
//   Vehicle + 0x80 -> name, Vehicle + 0xB0 -> team
//   VehicleGameLogic + 0xA8 -> VehicleDescr, +0x1B8 -> HP
//
// The historical module root is deliberately not used. This script searches
// the hash-bound current executable for the object relationship and member
// access shape. Matrix-shaped matches are retained but penalized; no result is
// a field or entity claim without manual decompilation and dynamic evidence.

import java.io.File;
import java.io.PrintWriter;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

public class FindVehiclePositionFamily extends GhidraScript {

    private static final int POINTER_OFFSET = 0x04;
    private static final int[] POSITION_OFFSETS = { 0x68, 0x6c, 0x70 };
    private static final int[] VEHICLE_SUPPORT_OFFSETS = {
        0x60, 0x64, 0x68, 0x6c, 0x70, 0x74, 0x78, 0x7c,
        0x80, 0x84, 0x88, 0x8c, 0x90, 0x94, 0x98, 0x9c,
        0xb0, 0x11c
    };
    private static final int[] GAME_LOGIC_SUPPORT_OFFSETS = { 0xa8, 0x1b8 };
    private static final int MAX_FORWARD_INSTRUCTIONS = 192;
    private static final int MAX_EVIDENCE_LINES = 24;
    private static final int MAX_PRINTED_CANDIDATES = 250;

    private static final String REGISTER_TOKEN =
            "(EAX|EBX|ECX|EDX|ESI|EDI)";
    private static final Pattern POINTER_LOAD = Pattern.compile(
            "^MOV\\s+" + REGISTER_TOKEN +
            ",(?:DWORD PTR )?\\[" + REGISTER_TOKEN +
            " \\+ 0X" + Integer.toHexString(POINTER_OFFSET).toUpperCase(Locale.ROOT) +
            "\\]$");
    private static final Pattern EXACT_MEMORY = Pattern.compile(
            "\\[" + REGISTER_TOKEN + "(?: \\+ 0X([0-9A-F]+))?\\]");
    private static final Pattern REGISTER_MOVE = Pattern.compile(
            "^MOV\\s+" + REGISTER_TOKEN + "," + REGISTER_TOKEN + "$");
    private static final Pattern FIRST_REGISTER_OPERAND = Pattern.compile(
            "^[A-Z][A-Z0-9]*\\s+" + REGISTER_TOKEN + "(?:,|$)");
    private static final Set<String> WRITES_FIRST_OPERAND = new HashSet<String>();

    static {
        Collections.addAll(WRITES_FIRST_OPERAND,
                "MOV", "MOVZX", "MOVSX", "LEA", "POP", "XOR", "ADD",
                "SUB", "AND", "OR", "IMUL", "SHL", "SHR", "SAR", "INC",
                "DEC", "NEG", "NOT");
    }

    private static final class PointerLoadSite {
        final int instructionIndex;
        final Address address;
        final String vehicleRegister;
        final String gameLogicRegister;
        final String rendered;

        PointerLoadSite(int instructionIndex, Address address,
                        String vehicleRegister, String gameLogicRegister,
                        String rendered) {
            this.instructionIndex = instructionIndex;
            this.address = address;
            this.vehicleRegister = vehicleRegister;
            this.gameLogicRegister = gameLogicRegister;
            this.rendered = rendered;
        }
    }

    private static final class Candidate {
        final Function function;
        final PointerLoadSite pointerLoad;
        final String baseRegister;
        final Set<Integer> vehicleOffsets;
        final Set<Integer> gameLogicOffsets;
        final List<String> evidence;
        final boolean anchorFunction;
        final boolean exactPositionTriple;
        final boolean matrixLike;
        final int score;

        Candidate(Function function, PointerLoadSite pointerLoad,
                  String baseRegister, Set<Integer> vehicleOffsets,
                  Set<Integer> gameLogicOffsets, List<String> evidence,
                  boolean anchorFunction) {
            this.function = function;
            this.pointerLoad = pointerLoad;
            this.baseRegister = baseRegister;
            this.vehicleOffsets = vehicleOffsets;
            this.gameLogicOffsets = gameLogicOffsets;
            this.evidence = evidence;
            this.anchorFunction = anchorFunction;
            this.exactPositionTriple = containsAll(vehicleOffsets, POSITION_OFFSETS);
            this.matrixLike = countMatrixOffsets(vehicleOffsets) >= 8;

            int positionCount = countPresent(vehicleOffsets, POSITION_OFFSETS);
            int calculated = positionCount * 25;
            if (pointerLoad != null) {
                calculated += 45;
            }
            if (vehicleOffsets.contains(Integer.valueOf(0x80))) {
                calculated += 10;
            }
            if (vehicleOffsets.contains(Integer.valueOf(0xb0))) {
                calculated += 15;
            }
            if (vehicleOffsets.contains(Integer.valueOf(0x11c))) {
                calculated += 10;
            }
            if (gameLogicOffsets.contains(Integer.valueOf(0xa8))) {
                calculated += 10;
            }
            if (gameLogicOffsets.contains(Integer.valueOf(0x1b8))) {
                calculated += 15;
            }
            if (anchorFunction) {
                calculated += 25;
            }
            if (matrixLike) {
                calculated -= 80;
            }
            this.score = calculated;
        }
    }

    @Override
    public void run() throws Exception {
        Listing listing = currentProgram.getListing();
        Address imageBase = currentProgram.getImageBase();
        Set<Address> anchorFunctions = findVehicleGameLogicAnchorFunctions();
        List<Candidate> candidates = new ArrayList<Candidate>();
        long functionsScanned = 0;
        long pointerLoadsSeen = 0;
        long fallbackTriplesSeen = 0;
        long directCandidatesSeen = 0;
        long directExactTriplesSeen = 0;
        long directExactNonMatrixTriplesSeen = 0;

        FunctionIterator functions = currentProgram.getFunctionManager()
                .getFunctions(true);
        while (functions.hasNext() && !monitor.isCancelled()) {
            Function function = functions.next();
            functionsScanned++;
            List<Instruction> instructions = collectInstructions(listing, function);
            if (instructions.isEmpty()) {
                continue;
            }

            boolean anchorFunction = anchorFunctions.contains(function.getEntryPoint());
            List<PointerLoadSite> pointerLoads = findPointerLoads(instructions);
            pointerLoadsSeen += pointerLoads.size();
            Set<String> directCandidateKeys = new HashSet<String>();

            for (PointerLoadSite pointerLoad : pointerLoads) {
                Candidate candidate = analyzePointerLoad(
                        function, instructions, pointerLoad, anchorFunction);
                if (candidate == null) {
                    continue;
                }
                candidates.add(candidate);
                directCandidatesSeen++;
                if (candidate.exactPositionTriple) {
                    directExactTriplesSeen++;
                    if (!candidate.matrixLike) {
                        directExactNonMatrixTriplesSeen++;
                    }
                }
                directCandidateKeys.add(function.getEntryPoint() + ":" +
                        candidate.baseRegister);
            }

            Map<String, Set<Integer>> wholeFunctionOffsets =
                    collectWholeFunctionOffsets(instructions);
            for (Map.Entry<String, Set<Integer>> entry : wholeFunctionOffsets.entrySet()) {
                if (!containsAll(entry.getValue(), POSITION_OFFSETS)) {
                    continue;
                }
                fallbackTriplesSeen++;
                String key = function.getEntryPoint() + ":" + entry.getKey();
                if (directCandidateKeys.contains(key)) {
                    continue;
                }
                List<String> evidence = collectEvidenceForBase(
                        instructions, entry.getKey(), MAX_EVIDENCE_LINES);
                candidates.add(new Candidate(function, null, entry.getKey(),
                        entry.getValue(), Collections.<Integer>emptySet(), evidence,
                        anchorFunction));
            }
        }

        Collections.sort(candidates, new Comparator<Candidate>() {
            @Override
            public int compare(Candidate left, Candidate right) {
                int direct = Boolean.compare(right.pointerLoad != null,
                        left.pointerLoad != null);
                if (direct != 0) {
                    return direct;
                }
                int score = Integer.compare(right.score, left.score);
                if (score != 0) {
                    return score;
                }
                return left.function.getEntryPoint()
                        .compareTo(right.function.getEntryPoint());
            }
        });

        String outPath = getEvidenceOutputPath("vehicle-position-family.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("=== Vehicle position candidate-family triage ===");
        writer.println("program=" + currentProgram.getName() +
                " imageBase=" + imageBase);
        writer.println("functionsScanned=" + functionsScanned);
        writer.println("vehicleGameLogicAnchorFunctions=" + anchorFunctions.size());
        writer.println("pointerLoadsAtPlus04=" + pointerLoadsSeen);
        writer.println("directCandidates=" + directCandidatesSeen);
        writer.println("directExactPositionTriples=" + directExactTriplesSeen);
        writer.println("directExactNonMatrixPositionTriples=" +
                directExactNonMatrixTriplesSeen);
        writer.println("fallbackSameBaseTriples=" + fallbackTriplesSeen);
        writer.println("candidates=" + candidates.size());
        writer.println("policy=direct +0x04 handoffs sort before same-base fallbacks; " +
                "matrix-shaped matches are penalized; manual decompilation remains required");

        int printed = 0;
        for (Candidate candidate : candidates) {
            if (printed >= MAX_PRINTED_CANDIDATES) {
                break;
            }
            writeCandidate(writer, imageBase, candidate, printed + 1);
            printed++;
        }

        writer.close();
        println("functionsScanned=" + functionsScanned +
                " pointerLoads=" + pointerLoadsSeen +
                " directCandidates=" + directCandidatesSeen +
                " directExactTriples=" + directExactTriplesSeen +
                " directExactNonMatrixTriples=" +
                directExactNonMatrixTriplesSeen +
                " fallbackTriples=" + fallbackTriplesSeen +
                " candidates=" + candidates.size());
        println("WROTE " + outPath);
    }

    private Candidate analyzePointerLoad(Function function,
                                         List<Instruction> instructions,
                                         PointerLoadSite pointerLoad,
                                         boolean anchorFunction) {
        Set<String> vehicleAliases = new HashSet<String>();
        vehicleAliases.add(pointerLoad.vehicleRegister);
        Set<Integer> vehicleOffsets = new LinkedHashSet<Integer>();
        Set<Integer> gameLogicOffsets = new LinkedHashSet<Integer>();
        List<String> evidence = new ArrayList<String>();

        int end = Math.min(instructions.size(),
                pointerLoad.instructionIndex + MAX_FORWARD_INSTRUCTIONS);
        // The pointer-load instruction establishes the first alias. Begin at
        // the following instruction so the generic destination-write logic
        // does not immediately remove that freshly established alias.
        for (int index = pointerLoad.instructionIndex + 1; index < end; index++) {
            Instruction instruction = instructions.get(index);
            String rendered = normalize(instruction.toString());

            Matcher memory = EXACT_MEMORY.matcher(rendered);
            while (memory.find()) {
                String register = memory.group(1);
                int offset = memory.group(2) == null
                        ? 0
                        : Integer.parseUnsignedInt(memory.group(2), 16);
                if (vehicleAliases.contains(register) &&
                        isInterestingVehicleOffset(offset)) {
                    vehicleOffsets.add(Integer.valueOf(offset));
                    addEvidence(evidence, instruction);
                }
                if (register.equals(pointerLoad.gameLogicRegister) &&
                        isInterestingGameLogicOffset(offset)) {
                    gameLogicOffsets.add(Integer.valueOf(offset));
                    addEvidence(evidence, instruction);
                }
            }

            updateAliases(vehicleAliases, rendered, instruction.getMnemonicString());
            if ("CALL".equalsIgnoreCase(instruction.getMnemonicString())) {
                vehicleAliases.remove("EAX");
                vehicleAliases.remove("ECX");
                vehicleAliases.remove("EDX");
            }
            if (vehicleAliases.isEmpty()) {
                break;
            }
        }

        int positionCount = countPresent(vehicleOffsets, POSITION_OFFSETS);
        boolean hasCorroboration = vehicleOffsets.contains(Integer.valueOf(0x80)) ||
                vehicleOffsets.contains(Integer.valueOf(0xb0)) ||
                vehicleOffsets.contains(Integer.valueOf(0x11c)) ||
                !gameLogicOffsets.isEmpty();
        if (positionCount < 2 && !hasCorroboration) {
            return null;
        }

        return new Candidate(function, pointerLoad, pointerLoad.vehicleRegister,
                vehicleOffsets, gameLogicOffsets, evidence, anchorFunction);
    }

    private List<PointerLoadSite> findPointerLoads(List<Instruction> instructions) {
        List<PointerLoadSite> result = new ArrayList<PointerLoadSite>();
        for (int index = 0; index < instructions.size(); index++) {
            Instruction instruction = instructions.get(index);
            String rendered = normalize(instruction.toString());
            Matcher matcher = POINTER_LOAD.matcher(rendered);
            if (matcher.matches()) {
                result.add(new PointerLoadSite(index, instruction.getAddress(),
                        matcher.group(1), matcher.group(2), rendered));
            }
        }
        return result;
    }

    private Map<String, Set<Integer>> collectWholeFunctionOffsets(
            List<Instruction> instructions) {
        Map<String, Set<Integer>> result = new HashMap<String, Set<Integer>>();
        for (Instruction instruction : instructions) {
            Matcher memory = EXACT_MEMORY.matcher(normalize(instruction.toString()));
            while (memory.find()) {
                if (memory.group(2) == null) {
                    continue;
                }
                String register = memory.group(1);
                int offset = Integer.parseUnsignedInt(memory.group(2), 16);
                if (!isInterestingVehicleOffset(offset)) {
                    continue;
                }
                Set<Integer> offsets = result.get(register);
                if (offsets == null) {
                    offsets = new LinkedHashSet<Integer>();
                    result.put(register, offsets);
                }
                offsets.add(Integer.valueOf(offset));
            }
        }
        return result;
    }

    private List<String> collectEvidenceForBase(List<Instruction> instructions,
                                                String baseRegister, int limit) {
        List<String> evidence = new ArrayList<String>();
        for (Instruction instruction : instructions) {
            Matcher memory = EXACT_MEMORY.matcher(normalize(instruction.toString()));
            while (memory.find()) {
                if (!baseRegister.equals(memory.group(1)) || memory.group(2) == null) {
                    continue;
                }
                int offset = Integer.parseUnsignedInt(memory.group(2), 16);
                if (isInterestingVehicleOffset(offset)) {
                    addEvidence(evidence, instruction);
                    break;
                }
            }
            if (evidence.size() >= limit) {
                break;
            }
        }
        return evidence;
    }

    private Set<Address> findVehicleGameLogicAnchorFunctions() throws Exception {
        Set<Address> result = new HashSet<Address>();
        Memory memory = currentProgram.getMemory();
        ReferenceManager references = currentProgram.getReferenceManager();
        byte[] needle = "VehicleGameLogic".getBytes(StandardCharsets.US_ASCII);
        Address cursor = memory.getMinAddress();
        while (cursor != null && !monitor.isCancelled()) {
            Address hit = memory.findBytes(cursor, needle, null, true, monitor);
            if (hit == null) {
                break;
            }
            addReferenceFunctions(result, references, hit);
            if (hit.getOffset() >= 8) {
                addReferenceFunctions(result, references, hit.subtract(8));
            }
            if (hit.equals(memory.getMaxAddress())) {
                break;
            }
            cursor = hit.add(1);
        }
        return result;
    }

    private void addReferenceFunctions(Set<Address> result,
                                       ReferenceManager references,
                                       Address target) {
        ReferenceIterator iterator = references.getReferencesTo(target);
        while (iterator.hasNext()) {
            Reference reference = iterator.next();
            Function function = currentProgram.getFunctionManager()
                    .getFunctionContaining(reference.getFromAddress());
            if (function != null) {
                result.add(function.getEntryPoint());
            }
        }
    }

    private List<Instruction> collectInstructions(Listing listing,
                                                  Function function) {
        List<Instruction> result = new ArrayList<Instruction>();
        InstructionIterator iterator = listing.getInstructions(function.getBody(), true);
        while (iterator.hasNext()) {
            result.add(iterator.next());
        }
        return result;
    }

    private void updateAliases(Set<String> aliases, String rendered,
                               String mnemonic) {
        Matcher move = REGISTER_MOVE.matcher(rendered);
        if (move.matches()) {
            String destination = move.group(1);
            String source = move.group(2);
            boolean sourceIsAlias = aliases.contains(source);
            aliases.remove(destination);
            if (sourceIsAlias) {
                aliases.add(destination);
            }
            return;
        }

        if (!WRITES_FIRST_OPERAND.contains(mnemonic.toUpperCase(Locale.ROOT))) {
            return;
        }
        Matcher destination = FIRST_REGISTER_OPERAND.matcher(rendered);
        if (destination.find()) {
            aliases.remove(destination.group(1));
        }
    }

    private void addEvidence(List<String> evidence, Instruction instruction) {
        if (evidence.size() >= MAX_EVIDENCE_LINES) {
            return;
        }
        String rendered = instruction.getAddress() + ": " + instruction.toString();
        if (!evidence.contains(rendered)) {
            evidence.add(rendered);
        }
    }

    private void writeCandidate(PrintWriter writer, Address imageBase,
                                Candidate candidate, int rank) {
        long rva = candidate.function.getEntryPoint().getOffset() -
                imageBase.getOffset();
        writer.println();
        writer.println("## rank=" + rank + " score=" + candidate.score +
                " function=" + candidate.function.getName() +
                " entry=" + candidate.function.getEntryPoint() +
                " rva=0x" + Long.toHexString(rva));
        writer.println("anchorFunction=" + candidate.anchorFunction +
                " exactPositionTriple=" + candidate.exactPositionTriple +
                " matrixLike=" + candidate.matrixLike);
        writer.println("baseRegister=" + candidate.baseRegister);
        if (candidate.pointerLoad == null) {
            writer.println("pointerLoad=none (same-base fallback only)");
        }
        else {
            writer.println("pointerLoad=" + candidate.pointerLoad.address +
                    ": " + candidate.pointerLoad.rendered +
                    " gameLogicRegister=" + candidate.pointerLoad.gameLogicRegister);
        }
        writer.println("vehicleOffsets=" + formatOffsets(candidate.vehicleOffsets));
        writer.println("gameLogicOffsets=" + formatOffsets(candidate.gameLogicOffsets));
        writer.println("evidence:");
        for (String line : candidate.evidence) {
            writer.println("  " + line);
        }
    }

    private static boolean containsAll(Set<Integer> haystack, int[] needles) {
        for (int needle : needles) {
            if (!haystack.contains(Integer.valueOf(needle))) {
                return false;
            }
        }
        return true;
    }

    private static int countPresent(Set<Integer> haystack, int[] needles) {
        int count = 0;
        for (int needle : needles) {
            if (haystack.contains(Integer.valueOf(needle))) {
                count++;
            }
        }
        return count;
    }

    private static int countMatrixOffsets(Set<Integer> offsets) {
        int count = 0;
        for (int offset = 0x60; offset <= 0x9c; offset += 4) {
            if (offsets.contains(Integer.valueOf(offset))) {
                count++;
            }
        }
        return count;
    }

    private static boolean isInterestingVehicleOffset(int offset) {
        for (int candidate : VEHICLE_SUPPORT_OFFSETS) {
            if (offset == candidate) {
                return true;
            }
        }
        return false;
    }

    private static boolean isInterestingGameLogicOffset(int offset) {
        for (int candidate : GAME_LOGIC_SUPPORT_OFFSETS) {
            if (offset == candidate) {
                return true;
            }
        }
        return false;
    }

    private static String formatOffsets(Set<Integer> offsets) {
        List<Integer> sorted = new ArrayList<Integer>(offsets);
        Collections.sort(sorted);
        List<String> rendered = new ArrayList<String>();
        for (Integer offset : sorted) {
            rendered.add("0x" + Integer.toHexString(offset.intValue()));
        }
        return rendered.toString();
    }

    private static String normalize(String value) {
        return value.toUpperCase(Locale.ROOT).replaceAll("\\s+", " ").trim();
    }

    private String getEvidenceOutputPath(String fileName) throws Exception {
        String configured = System.getenv("WOTB_READER_GHIDRA_OUTPUT_DIR");
        File directory = configured == null || configured.trim().isEmpty()
                ? new File(System.getProperty("user.dir"), ".build\\ghidra-evidence")
                : new File(configured);
        if (!directory.isDirectory() && !directory.mkdirs()) {
            throw new IllegalStateException("Could not create Ghidra evidence directory");
        }
        return new File(directory, fileName).getAbsolutePath();
    }
}
