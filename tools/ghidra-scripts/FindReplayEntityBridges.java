// FindReplayEntityBridges.java - map named replay code toward the current
// VehicleGameLogic entity surface without guessing position displacements.
//
// This is a hash-bound static triage tool. It reports:
//   * defined strings that identify replay loading/playback code;
//   * code references to those strings;
//   * the current VehicleGameLogic vtable methods and entity-getter callers;
//   * bounded direct-call graph intersections between both sets.
//
// Direct-call reachability does not prove packet semantics. Virtual calls,
// callbacks, and table-driven dispatch can legitimately prevent a bridge from
// appearing. Every candidate still requires decompiler/data-flow review.

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Queue;
import java.util.Set;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

public class FindReplayEntityBridges extends GhidraScript {

    private static final long VEHICLE_GAME_LOGIC_VTABLE_RVA = 0x0327da50L;
    private static final int VEHICLE_GAME_LOGIC_SLOT_COUNT = 79;
    private static final long ENTITY_GETTER_RVA = 0x0031b560L;
    private static final long REPLAY_PLAYER_VTABLE_RVA = 0x03253c18L;
    private static final int REPLAY_PLAYER_SLOT_COUNT = 18;
    private static final long BLITZ_HANDLER_VTABLE_RVA = 0x0324dd90L;
    private static final int BLITZ_HANDLER_SLOT_COUNT = 23;
    private static final int MAX_GRAPH_DEPTH = 7;
    private static final int MAX_GRAPH_NODES = 100000;
    private static final int MAX_REPORTED_STRINGS = 500;
    private static final int MAX_REPORTED_BRIDGES = 100;

    private static final String[] REPLAY_KEYWORDS = {
        "start_replay_local",
        "stop_replay_local",
        "start replay event",
        "stop replay event",
        ".wotbreplay",
        "data.wotreplay",
        "replayrecorder",
        "replayplayer",
        "replaycontroller",
        "replay manager",
        "replaymanager",
        "loadgamescene",
        "load game scene",
        "replay"
    };

    private static final String[] ENTITY_KEYWORDS = {
        "vehiclegamelogic::",
        "vehiclegamelogic",
        "onenterworld",
        "onleaveworld",
        "set_position",
        "setposition",
        "position update"
    };

    private static final class StringAnchor {
        final Address address;
        final String value;
        final String category;
        final Set<Function> functions;

        StringAnchor(Address address, String value, String category,
                     Set<Function> functions) {
            this.address = address;
            this.value = value;
            this.category = category;
            this.functions = functions;
        }
    }

    private static final class Visit {
        final Function function;
        final int depth;
        final Function parent;
        final Function source;

        Visit(Function function, int depth, Function parent, Function source) {
            this.function = function;
            this.depth = depth;
            this.parent = parent;
            this.source = source;
        }
    }

    private static final class Bridge {
        final Function intersection;
        final Visit forward;
        final Visit reverse;

        Bridge(Function intersection, Visit forward, Visit reverse) {
            this.intersection = intersection;
            this.forward = forward;
            this.reverse = reverse;
        }

        int distance() {
            return forward.depth + reverse.depth;
        }
    }

    @Override
    public void run() throws Exception {
        Address imageBase = currentProgram.getImageBase();
        FunctionManager functions = currentProgram.getFunctionManager();
        ReferenceManager references = currentProgram.getReferenceManager();

        List<StringAnchor> anchors = collectStringAnchors(functions, references);
        Set<Function> replaySources = new LinkedHashSet<Function>();
        Set<Function> entityStringFunctions = new LinkedHashSet<Function>();
        for (StringAnchor anchor : anchors) {
            if (anchor.category.equals("replay")) {
                replaySources.addAll(anchor.functions);
            } else {
                entityStringFunctions.addAll(anchor.functions);
            }
        }

        Address getterAddress = imageBase.add(ENTITY_GETTER_RVA);
        Function getter = functions.getFunctionAt(getterAddress);
        Set<Function> getterCallers = directCallers(getterAddress, functions,
                references);
        Set<Function> vtableMethods = readVtableMethods(imageBase, functions,
                VEHICLE_GAME_LOGIC_VTABLE_RVA,
                VEHICLE_GAME_LOGIC_SLOT_COUNT);
        Set<Function> replayPlayerMethods = readVtableMethods(imageBase,
                functions, REPLAY_PLAYER_VTABLE_RVA,
                REPLAY_PLAYER_SLOT_COUNT);
        Set<Function> blitzHandlerMethods = readVtableMethods(imageBase,
                functions, BLITZ_HANDLER_VTABLE_RVA,
                BLITZ_HANDLER_SLOT_COUNT);
        Set<Function> replayPlayerVtableRefs = referringFunctions(
                imageBase.add(REPLAY_PLAYER_VTABLE_RVA), functions,
                references);
        Set<Function> blitzHandlerVtableRefs = referringFunctions(
                imageBase.add(BLITZ_HANDLER_VTABLE_RVA), functions,
                references);

        Set<Function> entityTargets = new LinkedHashSet<Function>();
        entityTargets.addAll(vtableMethods);
        entityTargets.addAll(getterCallers);
        entityTargets.addAll(entityStringFunctions);
        if (getter != null) {
            entityTargets.add(getter);
        }

        Map<Function, Visit> forward = traverse(replaySources, true);
        Map<Function, Visit> reverse = traverse(entityTargets, false);
        List<Bridge> bridges = new ArrayList<Bridge>();
        for (Map.Entry<Function, Visit> entry : forward.entrySet()) {
            Visit reverseVisit = reverse.get(entry.getKey());
            if (reverseVisit != null) {
                bridges.add(new Bridge(entry.getKey(), entry.getValue(),
                        reverseVisit));
            }
        }
        Collections.sort(bridges, new Comparator<Bridge>() {
            @Override
            public int compare(Bridge left, Bridge right) {
                int distance = Integer.compare(left.distance(), right.distance());
                if (distance != 0) {
                    return distance;
                }
                return left.intersection.getEntryPoint().compareTo(
                        right.intersection.getEntryPoint());
            }
        });

        String outPath = getEvidenceOutputPath("replay-entity-bridges.txt");
        PrintWriter writer = new PrintWriter(new File(outPath));
        writer.println("=== program: " + currentProgram.getName() +
                " image_base=" + imageBase + " ===");
        writer.println("executable_sha256=" + executableHash());
        writer.println("heuristic_only=true");
        writer.println("direct_calls_only=true");
        writer.println("max_graph_depth=" + MAX_GRAPH_DEPTH);
        writer.println("vtable_rva=0x" +
                Long.toHexString(VEHICLE_GAME_LOGIC_VTABLE_RVA));
        writer.println("replay_player_vtable_rva=0x" +
                Long.toHexString(REPLAY_PLAYER_VTABLE_RVA));
        writer.println("blitz_handler_vtable_rva=0x" +
                Long.toHexString(BLITZ_HANDLER_VTABLE_RVA));
        writer.println("entity_getter_rva=0x" +
                Long.toHexString(ENTITY_GETTER_RVA));
        writer.println("string_anchors=" + anchors.size() +
                " replay_source_functions=" + replaySources.size() +
                " entity_string_functions=" + entityStringFunctions.size());
        writer.println("vtable_methods=" + vtableMethods.size() +
                " getter_callers=" + getterCallers.size() +
                " entity_targets=" + entityTargets.size());
        writer.println("replay_player_methods=" + replayPlayerMethods.size() +
                " replay_player_vtable_ref_functions=" +
                replayPlayerVtableRefs.size());
        writer.println("blitz_handler_methods=" + blitzHandlerMethods.size() +
                " blitz_handler_vtable_ref_functions=" +
                blitzHandlerVtableRefs.size());
        writer.println("forward_nodes=" + forward.size() +
                " reverse_nodes=" + reverse.size() +
                " intersections=" + bridges.size());

        writer.println("");
        writer.println("## Replay/entity string anchors");
        int shownStrings = 0;
        for (StringAnchor anchor : anchors) {
            if (shownStrings >= MAX_REPORTED_STRINGS) {
                writer.println("... truncated at " + MAX_REPORTED_STRINGS +
                        " string anchors");
                break;
            }
            writer.println("");
            writer.println("### category=" + anchor.category + " address=" +
                    anchor.address + " value=" + sanitize(anchor.value));
            if (anchor.functions.isEmpty()) {
                writer.println("  code_refs=(none)");
            } else {
                for (Function function : anchor.functions) {
                    writer.println("  code_ref " + formatFunction(function,
                            imageBase));
                }
            }
            shownStrings++;
        }

        writer.println("");
        writer.println("## VehicleGameLogic vtable methods");
        for (Function function : vtableMethods) {
            writer.println("  " + formatFunction(function, imageBase));
        }

        writer.println("");
        writer.println("## Direct entity-getter callers");
        for (Function function : getterCallers) {
            writer.println("  " + formatFunction(function, imageBase));
        }

        writer.println("");
        writer.println("## ReplayPlayer vtable methods and construction refs");
        for (Function function : replayPlayerMethods) {
            writer.println("  method " + formatFunction(function, imageBase));
        }
        for (Function function : replayPlayerVtableRefs) {
            writer.println("  vtable_ref " + formatFunction(function, imageBase));
        }

        writer.println("");
        writer.println("## BlitzServerMessageHandler vtable methods and construction refs");
        for (Function function : blitzHandlerMethods) {
            writer.println("  method " + formatFunction(function, imageBase));
        }
        for (Function function : blitzHandlerVtableRefs) {
            writer.println("  vtable_ref " + formatFunction(function, imageBase));
        }

        writer.println("");
        writer.println("## Bounded direct-call bridge intersections");
        int shownBridges = 0;
        for (Bridge bridge : bridges) {
            if (shownBridges >= MAX_REPORTED_BRIDGES) {
                writer.println("... truncated at " + MAX_REPORTED_BRIDGES +
                        " bridges");
                break;
            }
            writer.println("");
            writer.println("### distance=" + bridge.distance() +
                    " forward_depth=" + bridge.forward.depth +
                    " reverse_depth=" + bridge.reverse.depth +
                    " intersection=" + formatFunction(bridge.intersection,
                            imageBase));
            writer.println("  replay_path=" + formatForwardPath(
                    bridge.intersection, forward, imageBase));
            writer.println("  entity_path=" + formatReversePath(
                    bridge.intersection, reverse, imageBase));
            shownBridges++;
        }

        writer.close();
        println("WROTE " + outPath + " anchors=" + anchors.size() +
                " intersections=" + bridges.size());
    }

    private List<StringAnchor> collectStringAnchors(FunctionManager functions,
                                                     ReferenceManager references) {
        List<StringAnchor> result = new ArrayList<StringAnchor>();
        DataIterator iterator = currentProgram.getListing().getDefinedData(true);
        while (iterator.hasNext() && !monitor.isCancelled()) {
            Data data = iterator.next();
            Object value = data.getValue();
            if (!(value instanceof String)) {
                continue;
            }
            String stringValue = (String)value;
            String lowered = stringValue.toLowerCase(Locale.ROOT);
            String category = matches(lowered, REPLAY_KEYWORDS) ? "replay" :
                    matches(lowered, ENTITY_KEYWORDS) ? "entity" : null;
            if (category == null) {
                continue;
            }
            Set<Function> referringFunctions = new LinkedHashSet<Function>();
            ReferenceIterator refs = references.getReferencesTo(data.getAddress());
            while (refs.hasNext()) {
                Reference reference = refs.next();
                Function function = functions.getFunctionContaining(
                        reference.getFromAddress());
                if (function != null && isExecutable(function.getEntryPoint())) {
                    referringFunctions.add(function);
                }
            }
            result.add(new StringAnchor(data.getAddress(), stringValue, category,
                    referringFunctions));
        }
        Collections.sort(result, new Comparator<StringAnchor>() {
            @Override
            public int compare(StringAnchor left, StringAnchor right) {
                int category = left.category.compareTo(right.category);
                if (category != 0) {
                    return category;
                }
                return left.address.compareTo(right.address);
            }
        });
        return result;
    }

    private Set<Function> readVtableMethods(Address imageBase,
                                            FunctionManager functions,
                                            long vtableRva,
                                            int slotCount)
            throws Exception {
        Set<Function> result = new LinkedHashSet<Function>();
        Address table = imageBase.add(vtableRva);
        for (int index = 0; index < slotCount; index++) {
            long pointer = Integer.toUnsignedLong(getInt(table.add(index * 4L)));
            Function function = functions.getFunctionAt(toAddr(pointer));
            if (function != null && isExecutable(function.getEntryPoint())) {
                result.add(function);
            }
        }
        return result;
    }

    private Set<Function> referringFunctions(Address target,
                                              FunctionManager functions,
                                              ReferenceManager references) {
        Set<Function> result = new LinkedHashSet<Function>();
        ReferenceIterator iterator = references.getReferencesTo(target);
        while (iterator.hasNext()) {
            Function function = functions.getFunctionContaining(
                    iterator.next().getFromAddress());
            if (function != null && isExecutable(function.getEntryPoint())) {
                result.add(function);
            }
        }
        return result;
    }

    private Set<Function> directCallers(Address target,
                                        FunctionManager functions,
                                        ReferenceManager references) {
        Set<Function> result = new LinkedHashSet<Function>();
        ReferenceIterator iterator = references.getReferencesTo(target);
        while (iterator.hasNext()) {
            Reference reference = iterator.next();
            if (!reference.getReferenceType().isCall()) {
                continue;
            }
            Function caller = functions.getFunctionContaining(
                    reference.getFromAddress());
            if (caller != null && isExecutable(caller.getEntryPoint())) {
                result.add(caller);
            }
        }
        return result;
    }

    private Map<Function, Visit> traverse(Set<Function> sources,
                                          boolean forward) {
        Map<Function, Visit> visits = new LinkedHashMap<Function, Visit>();
        Queue<Function> queue = new ArrayDeque<Function>();
        for (Function source : sources) {
            if (source == null || visits.containsKey(source)) {
                continue;
            }
            visits.put(source, new Visit(source, 0, null, source));
            queue.add(source);
        }
        while (!queue.isEmpty() && visits.size() < MAX_GRAPH_NODES &&
                !monitor.isCancelled()) {
            Function current = queue.remove();
            Visit currentVisit = visits.get(current);
            if (currentVisit.depth >= MAX_GRAPH_DEPTH) {
                continue;
            }
            Set<Function> neighbors = forward ? directCallees(current) :
                    directCallers(current.getEntryPoint(),
                            currentProgram.getFunctionManager(),
                            currentProgram.getReferenceManager());
            for (Function neighbor : neighbors) {
                if (visits.containsKey(neighbor)) {
                    continue;
                }
                visits.put(neighbor, new Visit(neighbor,
                        currentVisit.depth + 1, current,
                        currentVisit.source));
                queue.add(neighbor);
                if (visits.size() >= MAX_GRAPH_NODES) {
                    break;
                }
            }
        }
        return visits;
    }

    private Set<Function> directCallees(Function function) {
        Set<Function> result = new LinkedHashSet<Function>();
        Listing listing = currentProgram.getListing();
        InstructionIterator iterator = listing.getInstructions(
                function.getBody(), true);
        while (iterator.hasNext()) {
            Instruction instruction = iterator.next();
            if (!instruction.getMnemonicString().equalsIgnoreCase("CALL")) {
                continue;
            }
            Address[] flows = instruction.getFlows();
            for (Address target : flows) {
                Function callee = currentProgram.getFunctionManager()
                        .getFunctionAt(target);
                if (callee != null && isExecutable(callee.getEntryPoint())) {
                    result.add(callee);
                }
            }
        }
        return result;
    }

    private static String formatForwardPath(Function intersection,
                                            Map<Function, Visit> visits,
                                            Address imageBase) {
        List<Function> path = new ArrayList<Function>();
        Function current = intersection;
        while (current != null) {
            path.add(current);
            Visit visit = visits.get(current);
            current = visit == null ? null : visit.parent;
        }
        Collections.reverse(path);
        return formatPath(path, imageBase);
    }

    private static String formatReversePath(Function intersection,
                                            Map<Function, Visit> visits,
                                            Address imageBase) {
        List<Function> path = new ArrayList<Function>();
        Function current = intersection;
        while (current != null) {
            path.add(current);
            Visit visit = visits.get(current);
            current = visit == null ? null : visit.parent;
        }
        return formatPath(path, imageBase);
    }

    private static String formatPath(List<Function> path, Address imageBase) {
        List<String> rendered = new ArrayList<String>();
        for (Function function : path) {
            rendered.add(formatFunction(function, imageBase));
        }
        return join(rendered, " -> ");
    }

    private static String formatFunction(Function function, Address imageBase) {
        long rva = function.getEntryPoint().getOffset() - imageBase.getOffset();
        return function.getName() + "@0x" + Long.toHexString(rva);
    }

    private static boolean matches(String value, String[] keywords) {
        for (String keyword : keywords) {
            if (value.contains(keyword)) {
                return true;
            }
        }
        return false;
    }

    private static String sanitize(String value) {
        String singleLine = value.replace('\r', ' ').replace('\n', ' ')
                .replace('\t', ' ');
        if (singleLine.length() > 240) {
            singleLine = singleLine.substring(0, 240) + "...";
        }
        return "\"" + singleLine.replace("\"", "'") + "\"";
    }

    private String executableHash() {
        String hash = currentProgram.getExecutableSHA256();
        return hash == null || hash.trim().isEmpty() ? "unknown" : hash;
    }

    private boolean isExecutable(Address address) {
        MemoryBlock block = currentProgram.getMemory().getBlock(address);
        return block != null && block.isExecute();
    }

    private static String join(List<String> values, String delimiter) {
        StringBuilder builder = new StringBuilder();
        for (int index = 0; index < values.size(); index++) {
            if (index > 0) {
                builder.append(delimiter);
            }
            builder.append(values.get(index));
        }
        return builder.toString();
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
