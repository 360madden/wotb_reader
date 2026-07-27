# Replay decoder blocker record

Documented: `2026-07-26T21:23:27Z`

This record preserves the major evidence-driven blockers encountered while
implementing the strict WotB 11.18 replay decoder. It is intentionally durable:
future decoder work should update or supersede these decisions through another
dated record instead of silently changing their meaning.

## BLK-0008: .NET 10 sealed `InvalidDataException`

- First encountered: `2026-07-26` during the initial decoder compile.
- Impact: the decoder could not compile because its custom format exception
  inherited from a framework exception that is sealed in .NET 10.
- Cause: the domain error taxonomy depended on inheritance from a concrete BCL
  I/O exception.
- Resolution: the replay format exception now derives from `Exception`, retains
  stable domain error codes, and is translated only at adapter boundaries.
- Why this resolution: decoder failures need a stable, framework-independent
  taxonomy; inheritance from a specific I/O implementation added no useful
  contract.
- Validation: the subsequent decoder build completed without warnings or
  errors before the later command-execution gate.
- Prevention: domain exceptions must not inherit concrete BCL I/O exception
  types solely to communicate category.

## BLK-0009: auxiliary protobuf record `#201`

- First encountered: `2026-07-26` while decoding the first private 11.18 replay.
- Impact: an otherwise valid replay was rejected with
  `replay.invalid_roster_account`.
- Evidence: a valid protocol-2 envelope contained a `#201` protobuf message
  without roster account field `#1`.
- Cause: the decoder assumed every message in that envelope represented a
  roster participant.
- Resolution: messages with that observed shape are retained as immutable
  `root.201.unmapped` raw evidence rather than rejected or assigned guessed
  identity semantics.
- Why this resolution: preserving bounded, hashed evidence keeps the replay
  usable and permits a later evidence-backed decoder upgrade without rewriting
  history.
- Validation: the affected replay decoded successfully after the change.
- Prevention: protobuf location alone is not sufficient semantic evidence;
  shape and cross-record evidence must support canonical projection.

## BLK-0010: schema-scoped subtype 48 and accountless roster members

- First encountered: `2026-07-26` during real-replay compatibility decoding.
- Impact: the initial projection exposed only six participants and emitted
  repeated subtype-48 warnings.
- Evidence: entity-method subtype numbers are schema-scoped. Only the message
  whose arguments matched the observed field-1 shape represented
  `updateArena2`; its full payload contained 14 entities. Some valid roster
  members did not carry account IDs.
- Cause: subtype 48 was treated as globally meaningful, and account identity
  had been treated as mandatory.
- Resolution:
  - recognize `updateArena2` only when its bounded message shape matches;
  - retain other subtype-48 calls as raw evidence;
  - model account identity as nullable;
  - never infer bot status from a missing account or from a player name; and
  - accept the first little-endian `u16` in the 15-byte stats blob as a tank
    descriptor only when it cross-validates against authoritative
    battle-result descriptors from the same replay.
- Why this resolution: the implementation uses the strongest evidence already
  present in the game artifacts while refusing unsafe global or identity
  assumptions.
- Validation: all ten private 11.18 replays produced 14 participants and real
  position timelines; observed position counts ranged from 15,409 to 36,605.
- Prevention: packet subtype interpretation must remain entity/schema scoped,
  and inferred fields require same-artifact cross-validation.

## BLK-0011: WotB 11.18 EOF sentinel

- First encountered: `2026-07-26` during packet-stream tail validation.
- Impact: complete streams ended with a false malformed-tail warning.
- Evidence: the final record was validly framed, aligned exactly with EOF, used
  type `0xffffffff`, had a zero clock, and carried a 16-byte payload.
- Cause: the strict packet plausibility checks did not recognize this terminal
  marker.
- Resolution: accept only this exact EOF-aligned sentinel and preserve it as a
  raw record; all ordinary packet plausibility and bounded-resynchronization
  rules remain strict.
- Why this resolution: it removes a demonstrated false positive without
  broadening acceptance of arbitrary malformed tails.
- Validation: a synthetic sentinel regression test was added. Its final
  compile/test execution is pending BLK-0007, the external command-execution
  gate.
- Prevention: terminal-format exceptions must be narrowly described and tested
  from observed framing evidence.

## Compatibility result at documentation time

- Ten private WotB `11.18.0.7` replays decoded without crashing before the
  final sentinel-hardening edit.
- Every replay yielded 14 participants and at least one real position timeline.
- Private paths, hashes, names, account IDs, and raw content were not recorded
  in this document or command output.
- Bot state remains `Unknown` unless explicit evidence exists.
- Entity-leave packets are not labeled as destruction because observed data
  showed that inference can be false.
- Unknown packets and fields remain referenced as immutable raw evidence.

## Pending verification

The following verification is required when BLK-0007 is lifted:

1. locked restore;
2. Release build of the replay decoder and Replay Inspector;
3. complete replay-decoder test project;
4. solution formatting verification; and
5. a repeat compatibility pass over the opt-in private replay directory.

## Compatibility pass repeat — 2026-07-27

Satisfies pending verification item 5, executed `2026-07-27T00:54Z` with
decoder `wotb-11.18-strict` (`0.1.0`) through the Release-built Replay
Inspector after a locked restore (build reported zero warnings).

- All ten private `11.18` replays in the opt-in local directory decoded
  successfully with exit code zero and no warnings.
- Every replay yielded 14 participants and real position timelines; observed
  position counts ranged from 15,409 to 36,605, exactly reproducing the bounds
  documented above.
- The inspector ran without `--include-sensitive`; no replay path, file name,
  hash, player name, account ID, or raw content was recorded.

