// FindType10DispatchTable.java - search initialized non-code memory for a
// possible replay dispatch-table relationship between type 10, payload length
// 49, and a code pointer.
//
// This is a bounded heuristic. A row is not a handler contract until code
// references and decompilation establish the table semantics.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

public class FindType10DispatchTable extends GhidraScript {

    private static final long TYPE_VALUE = 10;
    private static final long LENGTH_VALUE = 49;
    private static final int NEIGHBOR_BYTES = 0x20;
    private static final int MAX_RESULTS = 500;

    private static final class Candidate {
        final Address typeAddress;
        final Address lengthAddress;
        final List<Address> codePointers;
        final List<String> references;

        Candidate(Address typeAddress, Address lengthAddress,
                  List<Address> codePointers, List<String> references) {
            this.typeAddress = typeAddress;
            this.lengthAddress = lengthAddress;
            this.codePointers = codePointers;
            this.references = references;
        }
    }

    @Override
    public void run() throws Exception {
        Memory memory = currentProgram.getMemory();
        Address imageBase = currentProgram.getImageBase();
        List<Candidate> candidates = new ArrayList<Candidate>();
        Set<String> seen = new HashSet<String>();
        long alignedDwordsScanned = 0;
        long typeDwordsFound = 0;

        for (MemoryBlock block : memory.getBlocks()) {
            if (monitor.isCancelled() || candidates.size() >= MAX_RESULTS) {
                break;
            }
            if (!block.isInitialized() || block.isExternalBlock() ||
                    block.isExecute()) {
                continue;
            }
            Address cursor = align4(block.getStart());
            while (cursor.compareTo(block.getEnd()) <= 0 &&
                    block.getEnd().subtract(cursor) >= 3 &&
                    candidates.size() < MAX_RESULTS) {
                if ((alignedDwordsScanned & 0x3ffff) == 0 &&
                        monitor.isCancelled()) {
                    break;
                }
                alignedDwordsScanned++;
                if (readUnsignedInt(memory, cursor) != TYPE_VALUE) {
                    cursor = cursor.add(4);
                    continue;
                }
                typeDwordsFound++;
                for (int delta = -NEIGHBOR_BYTES; delta <= NEIGHBOR_BYTES;
                     delta += 4) {
                    if (delta == 0) {
                        continue;
                    }
                    Address lengthAddress;
                    try {
                        lengthAddress = cursor.add(delta);
                    } catch (Exception exception) {
                        continue;
                    }
                    if (!block.contains(lengthAddress) ||
                            block.getEnd().subtract(lengthAddress) < 3 ||
                            readUnsignedInt(memory, lengthAddress) !=
                                    LENGTH_VALUE) {
                        continue;
                    }
                    String key = cursor + ":" + lengthAddress;
                    if (!seen.add(key)) {
                        continue;
                    }
                    List<Address> codePointers = findCodePointers(memory, block,
                            cursor);
                    if (codePointers.isEmpty()) {
                        continue;
                    }
                    List<String> references = findReferences(cursor,
                            lengthAddress);
                    candidates.add(new Candidate(cursor, lengthAddress,
                            codePointers, references));
                }
                cursor = cursor.add(4);
            }
        }

        String outPath = getEvidenceOutputPath("type10-dispatch-table.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("=== program: " + currentProgram.getName() +
                " image_base=" + imageBase + " ===");
        writer.println("aligned_dwords_scanned=" + alignedDwordsScanned);
        writer.println("type_10_dwords=" + typeDwordsFound);
        writer.println("type_length_code_candidates=" + candidates.size());
        writer.println("neighbor_bytes=0x" +
                Integer.toHexString(NEIGHBOR_BYTES));
        writer.println("heuristic_only=true");

        for (Candidate candidate : candidates) {
            writer.println("");
            writer.println("### type_rva=0x" +
                    Long.toHexString(candidate.typeAddress.subtract(imageBase)) +
                    " length_rva=0x" +
                    Long.toHexString(candidate.lengthAddress.subtract(imageBase)));
            writer.println("relative_delta=" +
                    candidate.lengthAddress.subtract(candidate.typeAddress));
            MemoryBlock candidateBlock = memory.getBlock(candidate.typeAddress);
            writer.println("block=" + (candidateBlock == null ? "unknown" :
                    candidateBlock.getName()));
            for (Address pointer : candidate.codePointers) {
                writer.println("  code_rva=0x" +
                        Long.toHexString(pointer.subtract(imageBase)));
            }
            if (candidate.references.isEmpty()) {
                writer.println("  references=(none)");
            } else {
                for (String reference : candidate.references) {
                    writer.println("  reference=" + reference);
                }
            }
            writeNeighborhood(writer, memory, candidate.typeAddress, imageBase);
        }

        writer.close();
        println("WROTE " + outPath + " candidates=" + candidates.size());
    }

    private void writeNeighborhood(PrintWriter writer, Memory memory,
                                   Address center, Address imageBase)
            throws Exception {
        writer.println("  neighborhood:");
        for (int delta = -0x30; delta <= 0x30; delta += 4) {
            Address slot;
            try {
                slot = center.add(delta);
            } catch (Exception exception) {
                continue;
            }
            MemoryBlock block = memory.getBlock(slot);
            if (block == null || !block.isInitialized() ||
                    block.getEnd().subtract(slot) < 3) {
                continue;
            }
            long value = readUnsignedInt(memory, slot);
            String classification = "";
            Address pointed = imageBase.getAddressSpace().getAddress(value);
            MemoryBlock pointedBlock = memory.getBlock(pointed);
            if (pointedBlock != null) {
                classification = pointedBlock.isExecute() ? " code-rva=0x" :
                        " data-rva=0x";
                classification += Long.toHexString(pointed.subtract(imageBase));
            }
            writer.println("    delta=" + delta + " value=0x" +
                    Long.toHexString(value) + classification);
        }
    }

    private List<Address> findCodePointers(Memory memory, MemoryBlock block,
                                           Address center) throws Exception {
        List<Address> result = new ArrayList<Address>();
        Address imageBase = currentProgram.getImageBase();
        for (int delta = -NEIGHBOR_BYTES; delta <= NEIGHBOR_BYTES; delta += 4) {
            Address slot;
            try {
                slot = center.add(delta);
            } catch (Exception exception) {
                continue;
            }
            if (!block.contains(slot) || block.getEnd().subtract(slot) < 3) {
                continue;
            }
            long raw = readUnsignedInt(memory, slot);
            Address target;
            try {
                target = imageBase.getAddressSpace().getAddress(raw);
            } catch (Exception exception) {
                continue;
            }
            MemoryBlock targetBlock = memory.getBlock(target);
            if (targetBlock != null && targetBlock.isExecute() &&
                    !result.contains(target)) {
                result.add(target);
            }
        }
        return result;
    }

    private List<String> findReferences(Address typeAddress,
                                        Address lengthAddress) {
        List<String> result = new ArrayList<String>();
        ReferenceManager references = currentProgram.getReferenceManager();
        addReferences(result, references.getReferencesTo(typeAddress));
        addReferences(result, references.getReferencesTo(lengthAddress));
        return result;
    }

    private static void addReferences(List<String> result,
                                      ReferenceIterator iterator) {
        while (iterator.hasNext() && result.size() < 32) {
            Reference reference = iterator.next();
            String rendered = reference.getFromAddress() + " " +
                    reference.getReferenceType();
            if (!result.contains(rendered)) {
                result.add(rendered);
            }
        }
    }

    private static long readUnsignedInt(Memory memory, Address address)
            throws Exception {
        return Integer.toUnsignedLong(memory.getInt(address));
    }

    private static Address align4(Address address) {
        long remainder = address.getOffset() & 3L;
        return remainder == 0 ? address : address.add(4 - remainder);
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
