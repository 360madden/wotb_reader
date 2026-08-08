// FindType10PositionConsumers.java - static triage for the verified type-10
// position-packet layout.
//
// The replay decoder proves this 49-byte layout:
//   int32 entity/space/vehicle at 0x00/0x04/0x08
//   float32 position XYZ at 0x0C/0x10/0x14
//   float32 velocity XYZ at 0x24/0x28/0x2C
//   byte flags at 0x30
//
// This script does not claim that a match is the packet consumer. It ranks
// executable functions that access several of those displacements through the
// same non-stack x86 base register. Raw disassembly remains ignored under
// .build/ghidra-evidence and candidates require manual/decompiler review.
//
// Usage: set WOTB_READER_GHIDRA_OUTPUT_DIR to the repository's ignored
// .build/ghidra-evidence directory, then run this post-script against the
// already analyzed project. See offline/commands.md for the full command.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
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
import ghidra.program.model.mem.MemoryBlock;

public class FindType10PositionConsumers extends GhidraScript {

    private static final int[] ENTITY_OFFSETS = { 0x00, 0x04, 0x08 };
    private static final int[] POSITION_OFFSETS = { 0x0c, 0x10, 0x14 };
    private static final int[] VELOCITY_OFFSETS = { 0x24, 0x28, 0x2c };
    private static final int FLAGS_OFFSET = 0x30;
    private static final int[] FRAMED_REQUIRED_OFFSETS = {
        0x00, 0x04, 0x08, 0x0c, 0x10, 0x14, 0x18, 0x1c, 0x20
    };
    private static final int[] FRAMED_VELOCITY_OFFSETS = { 0x30, 0x34, 0x38 };
    private static final int FRAMED_FLAGS_OFFSET = 0x3c;
    private static final Set<Integer> INTERESTING = buildInterestingOffsets();
    private static final Pattern MEMORY = Pattern.compile("\\[([^\\]]+)\\]");
    private static final Pattern REGISTER =
            Pattern.compile("\\b(EAX|EBX|ECX|EDX|ESI|EDI)\\b");
    private static final Pattern PLUS_HEX =
            Pattern.compile("\\+\\s*0x([0-9a-fA-F]+)",
                    Pattern.CASE_INSENSITIVE);
    private static final Pattern PACKET_LENGTH_IMMEDIATE =
            Pattern.compile("(?<![0-9A-F])0X31(?![0-9A-F])");
    private static final Pattern TYPE_IMMEDIATE =
            Pattern.compile("(?<![0-9A-F])0XA(?![0-9A-F])");

    private static final class MemoryHit {
        final int instructionIndex;
        final int offset;
        final String line;

        MemoryHit(int instructionIndex, int offset, String line) {
            this.instructionIndex = instructionIndex;
            this.offset = offset;
            this.line = line;
        }
    }

    private static final class BaseEvidence {
        final List<MemoryHit> hits = new ArrayList<MemoryHit>();
    }

    private static final class Candidate {
        final Function function;
        final String layoutKind;
        final String baseRegister;
        final Set<Integer> offsets;
        final List<String> lines;
        final boolean hasPacketLengthImmediate;
        final boolean hasTypeImmediate;
        final int score;

        Candidate(Function function, String layoutKind, String baseRegister,
                  Set<Integer> offsets,
                  List<String> lines,
                  boolean hasPacketLengthImmediate, boolean hasTypeImmediate) {
            this.function = function;
            this.layoutKind = layoutKind;
            this.baseRegister = baseRegister;
            this.offsets = offsets;
            this.lines = lines;
            this.hasPacketLengthImmediate = hasPacketLengthImmediate;
            this.hasTypeImmediate = hasTypeImmediate;
            this.score = score(layoutKind, offsets, hasPacketLengthImmediate,
                    hasTypeImmediate);
        }
    }

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        Listing listing = currentProgram.getListing();
        List<Candidate> candidates = new ArrayList<Candidate>();

        FunctionIterator functions = currentProgram.getFunctionManager()
                .getFunctions(true);
        int functionsScanned = 0;
        while (functions.hasNext() && !monitor.isCancelled()) {
            Function function = functions.next();
            if (!isExecutable(function.getEntryPoint())) {
                continue;
            }
            functionsScanned++;
            Map<String, BaseEvidence> byBase = new HashMap<String, BaseEvidence>();
            List<Integer> packetLengthIndices = new ArrayList<Integer>();
            List<Integer> typeIndices = new ArrayList<Integer>();

            InstructionIterator instructions = listing.getInstructions(
                    function.getBody(), true);
            int instructionIndex = 0;
            while (instructions.hasNext()) {
                Instruction instruction = instructions.next();
                String rendered = instruction.toString().toUpperCase(Locale.ROOT);
                String withoutMemory = MEMORY.matcher(rendered).replaceAll("[]");
                if (PACKET_LENGTH_IMMEDIATE.matcher(withoutMemory).find()) {
                    packetLengthIndices.add(instructionIndex);
                }
                if (TYPE_IMMEDIATE.matcher(withoutMemory).find()) {
                    typeIndices.add(instructionIndex);
                }
                collectMemoryEvidence(instruction, rendered, instructionIndex,
                        byBase);
                instructionIndex++;
            }

            for (Map.Entry<String, BaseEvidence> entry : byBase.entrySet()) {
                Candidate candidate = buildBestWindow(function, entry.getKey(),
                        entry.getValue(), packetLengthIndices, typeIndices);
                if (candidate != null) {
                    candidates.add(candidate);
                }
            }
        }

        Collections.sort(candidates, new Comparator<Candidate>() {
            @Override
            public int compare(Candidate left, Candidate right) {
                int scoreOrder = Integer.compare(right.score, left.score);
                if (scoreOrder != 0) {
                    return scoreOrder;
                }
                return left.function.getEntryPoint().compareTo(
                        right.function.getEntryPoint());
            }
        });

        String outPath = getEvidenceOutputPath("type10-position-consumers.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("=== program: " + currentProgram.getName() +
                " image_base=" + imageBase + " ===");
        writer.println("functions_scanned=" + functionsScanned +
                " candidates=" + candidates.size());
        writer.println("heuristic_only=true");
        writer.println("payload_position_offsets=0x0c,0x10,0x14");
        writer.println("framed_record_required=0x00..0x20_header_ids_xyz");
        writer.println("local_window_instructions=128");
        writer.println("supporting_offsets_are_ranking_only=true");
        int framedCount = 0;
        int framedWithLength = 0;
        int framedWithType = 0;
        int framedWithBoth = 0;
        for (Candidate candidate : candidates) {
            if (!candidate.layoutKind.equals("framed-record")) {
                continue;
            }
            framedCount++;
            framedWithLength += candidate.hasPacketLengthImmediate ? 1 : 0;
            framedWithType += candidate.hasTypeImmediate ? 1 : 0;
            framedWithBoth += candidate.hasPacketLengthImmediate &&
                    candidate.hasTypeImmediate ? 1 : 0;
        }
        writer.println("framed_candidates=" + framedCount +
                " with_length_0x31=" + framedWithLength +
                " with_type_0x0a=" + framedWithType +
                " with_both=" + framedWithBoth);

        int shown = 0;
        for (Candidate candidate : candidates) {
            if (shown >= 100) {
                writer.println("... truncated at 100 candidates");
                break;
            }
            long rva = candidate.function.getEntryPoint().getOffset() -
                    imageBase.getOffset();
            writer.println("");
            writer.println("### score=" + candidate.score + " rva=0x" +
                    Long.toHexString(rva) + " function=" +
                    candidate.function.getName() + " layout=" +
                    candidate.layoutKind + " base=" +
                    candidate.baseRegister);
            writer.println("offsets=" + formatOffsets(candidate.offsets));
            writer.println("has_length_0x31=" +
                    candidate.hasPacketLengthImmediate +
                    " has_type_0x0a=" + candidate.hasTypeImmediate);
            for (String line : candidate.lines) {
                writer.println("  " + line);
            }
            shown++;
        }

        writer.close();
        println("WROTE " + outPath + " candidates=" + candidates.size());
    }

    private Candidate buildBestWindow(Function function, String baseRegister,
                                      BaseEvidence evidence,
                                      List<Integer> packetLengthIndices,
                                      List<Integer> typeIndices) {
        Candidate best = null;
        for (MemoryHit anchor : evidence.hits) {
            if (anchor.offset != POSITION_OFFSETS[0] &&
                    anchor.offset != 0x18) {
                continue;
            }
            int firstIndex = anchor.instructionIndex - 32;
            int lastIndex = anchor.instructionIndex + 95;
            Set<Integer> offsets = new HashSet<Integer>();
            List<String> lines = new ArrayList<String>();
            for (MemoryHit hit : evidence.hits) {
                if (hit.instructionIndex < firstIndex ||
                        hit.instructionIndex > lastIndex) {
                    continue;
                }
                offsets.add(hit.offset);
                if (lines.size() < 32) {
                    lines.add(hit.line);
                }
            }
            boolean payloadLayout = countPresent(offsets, POSITION_OFFSETS) ==
                    POSITION_OFFSETS.length;
            boolean framedLayout = countPresent(offsets,
                    FRAMED_REQUIRED_OFFSETS) == FRAMED_REQUIRED_OFFSETS.length;
            if (!payloadLayout && !framedLayout) {
                continue;
            }
            boolean hasLength = containsIndex(packetLengthIndices, firstIndex,
                    lastIndex);
            boolean hasType = containsIndex(typeIndices, firstIndex, lastIndex);
            String layoutKind = framedLayout ? "framed-record" : "payload";
            Candidate candidate = new Candidate(function, layoutKind,
                    baseRegister, offsets, lines, hasLength, hasType);
            if (best == null || candidate.score > best.score) {
                best = candidate;
            }
        }
        return best;
    }

    private static boolean containsIndex(List<Integer> indices, int first,
                                         int last) {
        for (Integer index : indices) {
            if (index >= first && index <= last) {
                return true;
            }
        }
        return false;
    }

    private void collectMemoryEvidence(Instruction instruction, String rendered,
                                       int instructionIndex,
                                       Map<String, BaseEvidence> byBase) {
        String mnemonic = instruction.getMnemonicString().toUpperCase(Locale.ROOT);
        if (mnemonic.equals("LEA")) {
            return;
        }
        int comma = rendered.indexOf(',');
        Matcher memoryMatcher = MEMORY.matcher(rendered);
        while (memoryMatcher.find()) {
            if (mnemonic.startsWith("MOV") && comma >= 0 &&
                    memoryMatcher.start() < comma) {
                continue;
            }
            String expression = memoryMatcher.group(1);
            Matcher registerMatcher = REGISTER.matcher(expression);
            if (!registerMatcher.find()) {
                continue;
            }
            String base = registerMatcher.group(1);
            int displacement = expression.trim().equals(base) ? 0 : -1;
            Matcher displacementMatcher = PLUS_HEX.matcher(expression);
            while (displacementMatcher.find()) {
                long parsed = Long.parseLong(displacementMatcher.group(1), 16);
                if (parsed <= Integer.MAX_VALUE) {
                    displacement = (int)parsed;
                }
            }
            if (!INTERESTING.contains(displacement)) {
                continue;
            }
            BaseEvidence evidence = byBase.get(base);
            if (evidence == null) {
                evidence = new BaseEvidence();
                byBase.put(base, evidence);
            }
            evidence.hits.add(new MemoryHit(instructionIndex, displacement,
                    instruction.getAddress() + ": " + instruction.toString()));
        }
    }

    private boolean isExecutable(Address address) {
        MemoryBlock block = currentProgram.getMemory().getBlock(address);
        return block != null && block.isExecute();
    }

    private static int score(String layoutKind, Set<Integer> offsets,
                             boolean hasLength,
                             boolean hasType) {
        if (layoutKind.equals("framed-record")) {
            int result = 40;
            result += countPresent(offsets, FRAMED_VELOCITY_OFFSETS) * 3;
            result += offsets.contains(FRAMED_FLAGS_OFFSET) ? 3 : 0;
            result += hasLength ? 100 : 0;
            result += hasType ? 20 : 0;
            return result;
        }
        int result = countPresent(offsets, POSITION_OFFSETS) * 4;
        result += countPresent(offsets, VELOCITY_OFFSETS) * 3;
        result += countPresent(offsets, ENTITY_OFFSETS) * 2;
        result += offsets.contains(FLAGS_OFFSET) ? 2 : 0;
        result += hasLength ? 3 : 0;
        result += hasType ? 1 : 0;
        return result;
    }

    private static int countPresent(Set<Integer> offsets, int[] expected) {
        int count = 0;
        for (int value : expected) {
            if (offsets.contains(value)) {
                count++;
            }
        }
        return count;
    }

    private static Set<Integer> buildInterestingOffsets() {
        Set<Integer> result = new HashSet<Integer>();
        for (int value : ENTITY_OFFSETS) {
            result.add(value);
        }
        for (int value : POSITION_OFFSETS) {
            result.add(value);
        }
        for (int value : VELOCITY_OFFSETS) {
            result.add(value);
        }
        result.add(FLAGS_OFFSET);
        for (int value : FRAMED_REQUIRED_OFFSETS) {
            result.add(value);
        }
        for (int value : FRAMED_VELOCITY_OFFSETS) {
            result.add(value);
        }
        result.add(FRAMED_FLAGS_OFFSET);
        return result;
    }

    private static String formatOffsets(Set<Integer> offsets) {
        List<Integer> ordered = new ArrayList<Integer>(offsets);
        Collections.sort(ordered);
        List<String> rendered = new ArrayList<String>();
        for (Integer offset : ordered) {
            rendered.add("0x" + Integer.toHexString(offset));
        }
        return rendered.toString();
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
