# Handoff - OD-072 moving cross-replay player position (2026-08-08)

## Outcome

The repository can now reliably read the replay player's position at the fixed
type-10 application event in the current 11.19.0.10 executable.

OD-072 repeated the unchanged `wotblitz.exe+0x022FA78D` / `F30F7E00` contract
on the other content-distinct replay and a fresh managed process after movement
was underway. The five-second request reached its 64-hit limit. All 64 hits had
readable replay-local entity IDs and finite XYZ, with fingerprint and cleanup
proven.

The replay viewpoint produced six distinct position triples. Two viewpoint
hits matched exact samples retained by the bounded decoded trajectory. Across
the capture, 12 decoded entity IDs matched, 13 hits matched retained samples
exactly, 41 were within one world unit, and 57 were within three units. The
remaining distance reflects the trajectory endpoint's 256-sample-per-entity
downsampling; it is not used as an exact-clock claim.

Combined with OD-071's exact static-window viewpoint match, this establishes
motion freshness and cross-replay/fresh-process repeatability for event-based
player-position reading. No private IDs or coordinates are included here.

## Evidence boundary

- Gate: `OfflineReplayVerified`
- Replay: content-distinct from OD-071; fresh managed process
- Target/register/displacement: unchanged from OD-069/070/071
- Capture: five seconds, maximum 64 hits
- Accepted hits: 64 (bounded/truncated at the configured limit)
- Successful replay-ID and finite XYZ reads: 64/64
- Opaque objects: 13
- Decoded entity-ID matches: 12
- Matched hits: 58
- Exact hits retained by downsampled ground truth: 13
- Hits within one / three world units: 41 / 57
- Captured entities with changing values: 12
- Viewpoint hits / distinct triples: 6 / 6
- Exact retained viewpoint matches: 2
- Instruction fingerprint: matched
- Cleanup: proven
- Processes remaining after shutdown: 0

## Proven capability

For this executable hash, a bounded helper can capture the replay-local entity
ID and packet-derived XYZ at the type-10 apply instruction. Same-entity decoded
comparison identifies the replay viewpoint, and that viewpoint changes during
playback. The proof now survives two content-distinct replays and fresh
processes.

This is reliable **event-based** player-position reading. It is not a stable
offset-based polling API. The helper still reads entity ID and XYZ as two reads
while one debug event suspends the process; hardware atomicity remains false.
No decoded replay clock is captured with each hit, so same-clock proof also
remains false.

## Workflow result

The changed workflow worked:

1. Treat stale community offsets as relationship clues, not addresses.
2. Build a hash-bound static semantic chain first.
3. Freeze one exact instruction/register contract in server/helper policy.
4. Prove it on a synthetic owned x86 target.
5. Spend one bounded live session on field identity, then a second only on the
   remaining motion/cross-replay question.
6. Preserve aggregate evidence and stop once the hypothesis is answered.

This avoided another broad heap-scan/write-trace loop and produced direct field
identity in two live sessions.

## Remaining gap: continuous polling

The overlay ultimately needs a stable way to obtain the viewpoint entity and
its current position without attaching a debugger to every type-10 event. The
best current family is already visible downstream:

- entity movement-filter pointer: `[entity+0x38]`;
- helper ring: 8 entries, stride `0x38`;
- current ring index: helper `+0x1C8`;
- position within each record: `+0x18`.

These offsets are static semantic facts, but no stable module-relative resolver
to the viewpoint entity/helper is proven. Do not publish them or update
`memory-offsets/11.19.0.10.json` yet.

## Next admissible work

Return offline/static first. Trace the proven entity resolver at RVA
`0x022FC850` back through its container/owner and identify how the replay
viewpoint entity ID selects the resolved entity. Then freeze a bounded read plan
for the movement-filter ring, including index bounds and wrap behavior. Only
after synthetic review may one further positively verified offline session test
that exact stable resolver/ring plan against the already proven event stream.

Do not repeat the type-10 execute capture unchanged. Do not resume broad scans,
stale community member reads, or the transform-fill branch.

## Privacy and shutdown

Tracked evidence is aggregate-only. It contains no process addresses, replay
entity IDs, XYZ values, names, account data, replay filenames/paths, capability
values, screenshots, or raw replay/memory bytes. The sanitized aggregate remains
in the ignored local data tree. Game, Host, helper, and debugger processes were
confirmed absent after the session.
