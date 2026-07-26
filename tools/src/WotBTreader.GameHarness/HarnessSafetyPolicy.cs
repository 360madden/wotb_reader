namespace WotBTreader.GameHarness;

/// <summary>
/// Bounded policy settings for replay-only game operations.
/// </summary>
public sealed record HarnessSafetyOptions(
    TimeSpan MaximumLifecycleEvidenceAge,
    TimeSpan MaximumArmLifetime,
    IReadOnlySet<string> AllowedLifecycleSources)
{
    public static HarnessSafetyOptions Default { get; } = new(
        MaximumLifecycleEvidenceAge: TimeSpan.FromSeconds(15),
        MaximumArmLifetime: TimeSpan.FromMinutes(2),
        AllowedLifecycleSources: new HashSet<string>(
            ["blitz-native-log"],
            StringComparer.Ordinal));
}

/// <summary>
/// Complete result of a fail-closed safety evaluation.
/// </summary>
public sealed record HarnessSafetyDecision(
    bool Allowed,
    string StableCode,
    IReadOnlyList<string> Reasons)
{
    public static HarnessSafetyDecision Permit() =>
        new(true, "harness.safety_passed", []);

    public static HarnessSafetyDecision Deny(string code, params string[] reasons) =>
        new(false, code, reasons);
}

/// <summary>
/// Verifies process identity, window ownership, integrity, lifecycle evidence,
/// replay-launch correlation, and optional one-shot input arming.
/// </summary>
public sealed class HarnessSafetyPolicy
{
    private readonly HarnessSafetyOptions _options;
    private readonly TimeProvider _timeProvider;

    public HarnessSafetyPolicy(
        HarnessSafetyOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? HarnessSafetyOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HarnessSafetyDecision Evaluate(
        GameProcessObservation? observation,
        ExpectedGameIdentity expectedIdentity,
        Guid launchCorrelationId,
        InputArm? arm,
        bool requireArm)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        if (observation is null || !observation.IsRunning)
        {
            return HarnessSafetyDecision.Deny(
                "harness.game_not_running",
                "The configured game process is not running.");
        }

        if (observation.ProcessId <= 0 || observation.WindowHandle == 0)
        {
            return HarnessSafetyDecision.Deny(
                "harness.invalid_window",
                "The observed process does not own a usable game window.");
        }

        if (!PathsEqual(observation.ExecutablePath, expectedIdentity.ExecutablePath))
        {
            return HarnessSafetyDecision.Deny(
                "harness.process_path_mismatch",
                "The observed executable path does not match the configured installation.");
        }

        if (!string.Equals(
                observation.ExecutableVersion,
                expectedIdentity.ExecutableVersion,
                StringComparison.Ordinal))
        {
            return HarnessSafetyDecision.Deny(
                "harness.process_version_mismatch",
                "The observed game version is not the explicitly supported build.");
        }

        if (!HashesEqual(observation.ExecutableSha256, expectedIdentity.ExecutableSha256))
        {
            return HarnessSafetyDecision.Deny(
                "harness.process_hash_mismatch",
                "The observed executable hash does not match the verified game image.");
        }

        if (!observation.IsForegroundWindow)
        {
            return HarnessSafetyDecision.Deny(
                "harness.window_not_foreground",
                "The verified game window is not the foreground window.");
        }

        if (observation.GameIntegrity == ProcessIntegrityLevel.Unknown
            || observation.HarnessIntegrity == ProcessIntegrityLevel.Unknown
            || observation.GameIntegrity != observation.HarnessIntegrity)
        {
            return HarnessSafetyDecision.Deny(
                "harness.integrity_mismatch",
                "Harness and game integrity levels are unknown or unequal.");
        }

        var lifecycle = observation.Lifecycle;
        if (lifecycle.State != ReplayLifecycleState.OfflineReplayActive)
        {
            return HarnessSafetyDecision.Deny(
                "harness.offline_replay_not_verified",
                "Current native-log evidence does not positively identify an offline replay.");
        }

        if (!_options.AllowedLifecycleSources.Contains(lifecycle.Source))
        {
            return HarnessSafetyDecision.Deny(
                "harness.untrusted_lifecycle_source",
                "Replay state was not derived from an approved native-log source.");
        }

        var evidenceAge = _timeProvider.GetUtcNow() - lifecycle.ObservedAtUtc;
        if (evidenceAge < TimeSpan.Zero || evidenceAge > _options.MaximumLifecycleEvidenceAge)
        {
            return HarnessSafetyDecision.Deny(
                "harness.lifecycle_evidence_stale",
                "Offline replay evidence is stale or has an invalid timestamp.");
        }

        if (lifecycle.ProcessId != observation.ProcessId)
        {
            return HarnessSafetyDecision.Deny(
                "harness.lifecycle_process_mismatch",
                "Offline replay evidence belongs to a different process.");
        }

        if (launchCorrelationId == Guid.Empty
            || lifecycle.LaunchCorrelationId != launchCorrelationId)
        {
            return HarnessSafetyDecision.Deny(
                "harness.launch_correlation_mismatch",
                "Offline replay evidence is not correlated with the requested replay launch.");
        }

        if (!requireArm)
        {
            return HarnessSafetyDecision.Permit();
        }

        if (arm is null)
        {
            return HarnessSafetyDecision.Deny(
                "harness.input_not_armed",
                "Game input is disabled until explicitly armed for this process.");
        }

        var now = _timeProvider.GetUtcNow();
        if (arm.ArmId == Guid.Empty
            || arm.ProcessId != observation.ProcessId
            || !HashesEqual(arm.ExecutableSha256, observation.ExecutableSha256))
        {
            return HarnessSafetyDecision.Deny(
                "harness.arm_identity_mismatch",
                "The input arm does not identify this process image.");
        }

        var armLifetime = arm.ExpiresAtUtc - arm.ArmedAtUtc;
        if (arm.ArmedAtUtc > now
            || arm.ExpiresAtUtc <= now
            || armLifetime <= TimeSpan.Zero
            || armLifetime > _options.MaximumArmLifetime)
        {
            return HarnessSafetyDecision.Deny(
                "harness.arm_expired",
                "The input arm is expired, not yet valid, or exceeds its maximum lifetime.");
        }

        return HarnessSafetyDecision.Permit();
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HashesEqual(string left, string right) =>
        left.Length == 64
        && right.Length == 64
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
