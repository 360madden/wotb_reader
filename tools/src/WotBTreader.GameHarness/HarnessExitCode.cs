namespace WotBTreader.GameHarness;

/// <summary>
/// Stable process exit codes for game-harness automation.
/// </summary>
public enum HarnessExitCode
{
    Success = 0,
    InvalidArguments = 2,
    UnsupportedCapability = 3,
    InvalidInput = 4,
    ConflictOrBusy = 5,
    InternalFailure = 10,
}
