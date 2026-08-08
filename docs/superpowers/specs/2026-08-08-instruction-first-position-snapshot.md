# Instruction-first player-position snapshot

Status: live mechanism proven; scale and local-translation hypotheses closed;
world-matrix translation is the next bounded target.

## Decision

Player-position discovery pivots from broad value scanning and transient-copy
write tracing to a version-pinned execute breakpoint at the already evidenced
game-code transform-fill instruction. This is a provenance change, not a faster
variant of FRESH45.

The production caller supplies only duration and accepted-hit bounds. The
coordinator owns all sensitive targeting:

| Item | Fixed policy |
|---|---|
| Game version | `11.19.0.10` |
| Executable identity | exact SHA-256 already recorded for that version |
| Module | unique `wotblitz.exe` main image |
| Instruction | RVA `0x7C39AB`, exact bytes `8B83A0000000` |
| Object register | EBX |
| Position read | one 12-byte `ReadProcessMemory` at checked EBX+`0x90` |
| Axes | X/Y/Z at read offsets +0/+4/+8 |
| Duration | 1-5 seconds |
| Accepted samples | 1-64 |
| Threads | at most 256 (raised from 128 after the first live process was measured at 164) |
| Result | at most 64 KiB |

## Authorization boundary

`GameSessionCoordinator` is the only production admission point. It snapshots
the exact managed child PID, creation identity, canonical executable path,
product version, executable hash, authorization generation, and revocation
token after confirming `OfflineReplayVerified`. The helper path is separately
configured with an exact SHA-256; missing, stale, or mismatched helpers fail
before target access.

The x86 production helper is a separate binary from the legacy PAGE_GUARD
interceptor and contains no raw-PID/write-capable mode. Its publish embeds the
exact SHA-256 of both the Host.Web apphost and managed Host.Web assembly. Before
target access, it independently verifies its actual parent PID, creation time,
apphost image/hash, sibling managed assembly/hash, and one-shot inherited pipe
capability. Caller-created pipes from another parent are rejected before
`OpenProcess` or debugger attach. Authorization loss signals cancellation from
before plan transmission onward. If cleanup cannot be proven, the coordinator
terminates the exact managed replay child by launch identity, independent of a
normal authorization-generation refresh.

The controlled publish script writes an owner-only local identity manifest only after the
Host build and helper publish succeed. Launch requires the manifest's helper,
Host EXE, and Host DLL hashes to match current files, then requires a
mode-specific JSON response containing a fresh nonce. The candidate helper
never self-approves its own expected hash.

## Native capture contract

The helper:

1. hard-pins the game version/hash/module/RVA/bytes/register/displacement in the
   helper itself, then revalidates process creation identity and image;
2. resolves exactly one target module and checks the RVA against PE
   `SizeOfImage`, an executable section, committed `MEM_IMAGE` memory, and the
   exact in-memory instruction bytes;
3. attaches with the default kill-on-debugger-exit containment retained, then
   consumes the initial `CREATE_PROCESS_DEBUG_EVENT` and revalidates that event
   handle before arming any thread;
4. preserves DR0-DR3, DR6, and DR7 for every current thread, rejects an occupied
   DR0, and arms new threads before their first user-mode instruction;
5. accepts only a first-chance `STATUS_SINGLE_STEP` whose exception address,
   EIP, and owned DR6 bit all match the target;
6. reads EBX and one contiguous 12-byte block while the debug event holds the
   process, clears its DR6 bit, sets the resume flag, and continues exactly
   once;
7. samples each object no more often than the server-fixed interval so the
   bounded report contains trajectories instead of an immediate per-frame
   burst;
8. restores and reads back the exact debug-register state before detach on
   max-hit, timeout, or cancellation.

The execute helper explicitly opens only query handles before attach; target
reads use the process handle supplied by the verified debug event. It does not
call `WriteProcessMemory`, `VirtualProtectEx`, or request VM write/operation
rights. The existing PAGE_GUARD interceptor remains in a different binary.

## Evidence semantics and privacy

The helper's internal pipe report may contain process-local addresses needed to
project evidence. `GameIntegration` replaces object addresses with
per-capture `object-NN` keys before anything reaches Host.Web or GameHarness.
The public result contains timestamps, XYZ values, read/finite status, module
RVA, and conservative proof flags. It contains no PID, heap address, full
path, instruction bytes, register dump, replay identity, capability, account,
player, chat, screenshot, or raw replay bytes.

Even a successful hit proves only that one register-derived object at the
pinned instruction had readable members at `+0x90/+0x94/+0x98` during the
same suspended debug event. It does not by itself prove:

- hardware atomicity of the three floats;
- alignment to an exact decoded replay clock;
- which object is the viewpoint player;
- a stable root or pointer chain;
- a publishable offset.

Those flags remain false until independent trajectory comparison and resolver
evidence justify changing them.

## Validation and live gate

Required before any live session:

```powershell
pwsh -NoProfile -File scripts/publish-instruction-snapshot-helper.ps1
pwsh -NoProfile -File tmpwotb-e2e/test-execute-snapshot-interceptor.ps1
```

The synthetic target owns an x86 object, executes the exact six instruction
bytes with EBX pointing to it, and changes all three floats. The test requires
multiple finite XYZ hits, bounded output, max-hit restore/detach, timeout
restore/detach, rejection of raw-PID/legacy modes, and rejection of a
caller-created pipe plan from a non-pinned parent before attach. Existing
guard-page tests remain regression coverage for the separate old binary.

The prior live rounds proved the breakpoint/cleanup path, raised the empirical
thread bound to 256, classified `+0x1C/+0x20/+0x24` as scale `(1,1,1)`, and
found no exact decoded-participant match for the changing local translation at
`+0x10/+0x14/+0x18`. The next live budget is one five-second capture of the
composed world-matrix translation at `+0x90/+0x94/+0x98` under a newly verified
managed offline replay. Harness output includes capture UTC. Group by object
key and compare the timestamped trajectory with decoded XYZ ground truth. A
no-hit/no-match closes that bounded hypothesis; a match permits one repeat on
the other replay but still does not authorize offset publication without a
stable resolver and the normal promotion checklist.
