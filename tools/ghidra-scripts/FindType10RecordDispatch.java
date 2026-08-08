// FindType10RecordDispatch.java - find direct checks of the replay event-record
// header for the verified type-10, 49-byte position payload.
//
// Framed event records are:
//   +0x00 uint32 payload length (0x31 for position)
//   +0x04 uint32 type (0x0A for position)
//   +0x08 float clock
//   +0x0C payload
//
// A result is only a dispatch candidate. Table-driven or generic parsing may
// legitimately produce no direct pair. Evidence is written to the ignored
// .build/ghidra-evidence directory.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
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

public class FindType10RecordDispatch extends GhidraScript {

    private static final Pattern MEMORY = Pattern.compile("\\[([^\\]]+)\\]");
    private static final Pattern REGISTER =
            Pattern.compile("\\b(EAX|EBX|ECX|EDX|ESI|EDI)\\b");
    private static final Pattern PLUS_HEX =
            Pattern.compile("\\+\\s*0x([0-9a-fA-F]+)",
                    Pattern.CASE_INSENSITIVE);
    private static final Pattern LENGTH_IMMEDIATE =
            Pattern.compile("(?<![0-9A-F])0X31(?![0-9A-F])");
    private static final Pattern TYPE_IMMEDIATE =
            Pattern.compile("(?<![0-9A-F])0XA(?![0-9A-F])");

    private static final class HeaderCheck {
        final int instructionIndex;
        final Address address;
        final String baseRegister;
        final int displacement;
        final boolean lengthCheck;
        final String rendered;

        HeaderCheck(int instructionIndex, Address address, String baseRegister,
                    int displacement, boolean lengthCheck, String rendered) {
            this.instructionIndex = instructionIndex;
            this.address = address;
            this.baseRegister = baseRegister;
            this.displacement = displacement;
            this.lengthCheck = lengthCheck;
            this.rendered = rendered;
        }
    }

    private static final class Pair {
        final Function function;
        final HeaderCheck length;
        final HeaderCheck type;
        final List<String> nearbyCalls;

        Pair(Function function, HeaderCheck length, HeaderCheck type,
             List<String> nearbyCalls) {
            this.function = function;
            this.length = length;
            this.type = type;
            this.nearbyCalls = nearbyCalls;
        }
    }

    private static final class LooseCheck {
        final Function function;
        final HeaderCheck check;
        final List<String> nearbyCalls;

        LooseCheck(Function function, HeaderCheck check,
                   List<String> nearbyCalls) {
            this.function = function;
            this.check = check;
            this.nearbyCalls = nearbyCalls;
        }
    }

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        Listing listing = currentProgram.getListing();
        List<Pair> pairs = new ArrayList<Pair>();
        List<LooseCheck> looseChecks = new ArrayList<LooseCheck>();
        int functionsScanned = 0;
        int looseLengthChecks = 0;
        int looseTypeChecks = 0;

        FunctionIterator functions = currentProgram.getFunctionManager()
                .getFunctions(true);
        while (functions.hasNext() && !monitor.isCancelled()) {
            Function function = functions.next();
            if (!isExecutable(function.getEntryPoint())) {
                continue;
            }
            functionsScanned++;
            List<Instruction> instructions = new ArrayList<Instruction>();
            List<HeaderCheck> checks = new ArrayList<HeaderCheck>();
            InstructionIterator iterator = listing.getInstructions(
                    function.getBody(), true);
            int index = 0;
            while (iterator.hasNext()) {
                Instruction instruction = iterator.next();
                instructions.add(instruction);
                HeaderCheck check = parseCheck(instruction, index);
                if (check != null) {
                    checks.add(check);
                    looseLengthChecks += check.lengthCheck ? 1 : 0;
                    looseTypeChecks += check.lengthCheck ? 0 : 1;
                }
                index++;
            }

            for (HeaderCheck length : checks) {
                if (!length.lengthCheck || length.displacement != 0) {
                    continue;
                }
                for (HeaderCheck type : checks) {
                    if (type.lengthCheck || type.displacement != 4 ||
                            !type.baseRegister.equals(length.baseRegister) ||
                            Math.abs(type.instructionIndex -
                                    length.instructionIndex) > 64) {
                        continue;
                    }
                    pairs.add(new Pair(function, length, type,
                            nearbyCalls(instructions,
                                    Math.min(length.instructionIndex,
                                            type.instructionIndex) - 16,
                                    Math.max(length.instructionIndex,
                                            type.instructionIndex) + 32)));
                }
            }
            for (HeaderCheck check : checks) {
                looseChecks.add(new LooseCheck(function, check,
                        nearbyCalls(instructions, check.instructionIndex - 12,
                                check.instructionIndex + 24)));
            }
        }

        String outPath = getEvidenceOutputPath("type10-record-dispatch.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("=== program: " + currentProgram.getName() +
                " image_base=" + imageBase + " ===");
        writer.println("functions_scanned=" + functionsScanned);
        writer.println("loose_length_0x31_checks=" + looseLengthChecks);
        writer.println("loose_type_0x0a_checks=" + looseTypeChecks);
        writer.println("same_base_header_pairs=" + pairs.size());
        writer.println("heuristic_only=true");

        int shown = 0;
        for (Pair pair : pairs) {
            if (shown >= 100) {
                writer.println("... truncated at 100 pairs");
                break;
            }
            long rva = pair.function.getEntryPoint().getOffset() -
                    imageBase.getOffset();
            writer.println("");
            writer.println("### rva=0x" + Long.toHexString(rva) +
                    " function=" + pair.function.getName() + " base=" +
                    pair.length.baseRegister);
            writer.println("  length " + pair.length.address + ": " +
                    pair.length.rendered);
            writer.println("  type   " + pair.type.address + ": " +
                    pair.type.rendered);
            if (pair.nearbyCalls.isEmpty()) {
                writer.println("  nearby_calls=(none)");
            } else {
                for (String call : pair.nearbyCalls) {
                    writer.println("  call   " + call);
                }
            }
            shown++;
        }

        writer.println("");
        writer.println("## Loose length checks (manual triage; not dispatch proof)");
        writeLooseChecks(writer, looseChecks, true, imageBase, 100);
        writer.println("");
        writer.println("## Loose type checks (first 100; not dispatch proof)");
        writeLooseChecks(writer, looseChecks, false, imageBase, 100);

        writer.close();
        println("WROTE " + outPath + " pairs=" + pairs.size());
    }

    private static void writeLooseChecks(PrintWriter writer,
                                         List<LooseCheck> checks,
                                         boolean lengthChecks,
                                         Address imageBase,
                                         int maximum) {
        int shown = 0;
        for (LooseCheck loose : checks) {
            if (loose.check.lengthCheck != lengthChecks) {
                continue;
            }
            if (shown >= maximum) {
                writer.println("... truncated at " + maximum + " checks");
                return;
            }
            long rva = loose.function.getEntryPoint().getOffset() -
                    imageBase.getOffset();
            writer.println("");
            writer.println("### rva=0x" + Long.toHexString(rva) +
                    " function=" + loose.function.getName() + " base=" +
                    loose.check.baseRegister + " displacement=0x" +
                    Integer.toHexString(loose.check.displacement));
            writer.println("  check  " + loose.check.address + ": " +
                    loose.check.rendered);
            for (String call : loose.nearbyCalls) {
                writer.println("  call   " + call);
            }
            shown++;
        }
    }

    private HeaderCheck parseCheck(Instruction instruction, int index) {
        if (!instruction.getMnemonicString().equalsIgnoreCase("CMP")) {
            return null;
        }
        String rendered = instruction.toString().toUpperCase(Locale.ROOT);
        String withoutMemory = MEMORY.matcher(rendered).replaceAll("[]");
        boolean length = LENGTH_IMMEDIATE.matcher(withoutMemory).find();
        boolean type = TYPE_IMMEDIATE.matcher(withoutMemory).find();
        if (length == type) {
            return null;
        }
        Matcher memoryMatcher = MEMORY.matcher(rendered);
        if (!memoryMatcher.find()) {
            return null;
        }
        String expression = memoryMatcher.group(1);
        Matcher registerMatcher = REGISTER.matcher(expression);
        if (!registerMatcher.find()) {
            return null;
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
        if (displacement < 0) {
            return null;
        }
        return new HeaderCheck(index, instruction.getAddress(), base,
                displacement, length, instruction.toString());
    }

    private static List<String> nearbyCalls(List<Instruction> instructions,
                                            int first, int last) {
        List<String> result = new ArrayList<String>();
        int start = Math.max(0, first);
        int end = Math.min(instructions.size() - 1, last);
        for (int index = start; index <= end && result.size() < 16; index++) {
            Instruction instruction = instructions.get(index);
            if (instruction.getMnemonicString().equalsIgnoreCase("CALL")) {
                result.add(instruction.getAddress() + ": " +
                        instruction.toString());
            }
        }
        return result;
    }

    private boolean isExecutable(Address address) {
        MemoryBlock block = currentProgram.getMemory().getBlock(address);
        return block != null && block.isExecute();
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
