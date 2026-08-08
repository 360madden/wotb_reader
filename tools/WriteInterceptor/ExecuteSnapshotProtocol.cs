namespace WotBTreader.WriteInterceptor;

internal sealed record ExecuteSnapshotPlan
{
    public int CoordinatorProcessId { get; init; }
    public long CoordinatorProcessStartIdentity { get; init; }
    public string CoordinatorCanonicalExecutablePath { get; init; } = string.Empty;
    public string CoordinatorExecutableSha256 { get; init; } = string.Empty;
    public string CoordinatorManagedAssemblyPath { get; init; } = string.Empty;
    public string CoordinatorManagedAssemblySha256 { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public long ProcessStartIdentity { get; init; }
    public string CanonicalExecutablePath { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;
    public string ExecutableSha256 { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public uint Rva { get; init; }
    public string ExpectedInstructionHex { get; init; } = string.Empty;
    public int ObjectDisplacement { get; init; } = 0x90;
    public int DurationMilliseconds { get; init; } = 5_000;
    public int MaxHits { get; init; } = 16;
    public int MinimumObjectSampleIntervalMilliseconds { get; init; } = 750;
    public bool SyntheticOwnedTarget { get; init; }
    public uint SyntheticTargetAddress { get; init; }
}

internal sealed record ExecuteSnapshotVector(
    bool ReadOk,
    int BytesRead,
    float? X,
    float? Y,
    float? Z,
    bool Finite);

internal sealed record ExecuteSnapshotHit(
    int Sequence,
    DateTimeOffset Utc,
    uint ThreadId,
    uint ExceptionAddress,
    uint ContextEip,
    uint Dr6,
    uint ObjectAddress,
    uint ReadAddress,
    ExecuteSnapshotVector Vector,
    bool SameDebugEvent,
    bool DebugEventProcessSuspended,
    bool SingleRead12Bytes,
    bool HardwareAtomicReadProven,
    bool SameDecodedClockProven,
    bool ViewpointIdentityProven,
    bool StableRootProven);

internal sealed record ExecuteSnapshotTarget(
    string ModuleName,
    uint ModuleBase,
    uint ModuleSize,
    uint Rva,
    uint ResolvedAddress,
    string ExpectedInstructionHex,
    string ActualInstructionHex,
    bool InstructionMatched,
    bool ExecutableImageSectionProven);

internal sealed record ExecuteSnapshotReport
{
    public string Schema { get; init; } = "wotbtreader.execute-object-snapshot.v1";
    public string Mode { get; init; } = "execute-object-snapshot";
    public string Status { get; init; } = "failed";
    public int ExitCode { get; init; } = 5;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset FinishedUtc { get; init; }
    public int DurationMilliseconds { get; init; }
    public int MaxHits { get; init; }
    public int MaxThreads { get; init; }
    public ExecuteSnapshotTarget? Target { get; init; }
    public string ObjectRegister { get; init; } = "ebx";
    public int ObjectDisplacement { get; init; } = 0x90;
    public int ThreadsSeen { get; init; }
    public int ThreadsArmed { get; init; }
    public int ThreadsFailed { get; init; }
    public int ThreadsRestored { get; init; }
    public int HitCount { get; init; }
    public int MatchingBreakpointEvents { get; init; }
    public bool Truncated { get; init; }
    public bool Attached { get; init; }
    public bool CoordinatorIdentityPinned { get; init; }
    public bool DebuggerExitTerminatesTarget { get; init; }
    public bool BootstrapBreakpointObserved { get; init; }
    public bool CleanupProven { get; init; }
    public bool Detached { get; init; }
    public IReadOnlyList<ExecuteSnapshotHit> Hits { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
