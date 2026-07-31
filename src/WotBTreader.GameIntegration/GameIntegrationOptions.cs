namespace WotBTreader.GameIntegration;

/// <summary>
/// Configures read-only discovery and bounded parsing of locally installed WotB resources.
/// Explicit roots are searched in list order. DLC roots derived from earlier user-data roots
/// take precedence over later roots and the base installation.
/// </summary>
public sealed class GameIntegrationOptions
{
    private const int MaximumConfiguredRoots = 64;
    private const int MaximumSupportedVersions = 64;
    private const int MaximumStoredDvplBytes = 256 * 1024 * 1024;
    private const int MaximumOutputDvplBytes = 512 * 1024 * 1024;
    private const long MaximumMetadataCharacters = 128L * 1024 * 1024;
    private const int MaximumMetadataCacheEntries = 64;
    private const int MaximumInitialLogScanBytes = 64 * 1024 * 1024;
    private const int MaximumLogReadBytesPerPass = 16 * 1024 * 1024;
    private const int MaximumLogLineCharacters = 256 * 1024;
    private const int MaximumTrackedLogFiles = 128;
    private const int MaximumLogEventChannelCapacity = 65_536;
    private const long MaximumReplayLaunchBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Gets explicit game installation roots to probe before optional defaults.</summary>
    public IReadOnlyList<string> GameInstallRoots { get; init; } = [];

    /// <summary>
    /// Gets explicit WotB user-data roots. A root may be the <c>wotblitz</c>,
    /// <c>packs</c>, or <c>DAVAProject</c> directory.
    /// </summary>
    public IReadOnlyList<string> UserDataRoots { get; init; } = [];

    /// <summary>Gets whether conventional per-user and Steam/WGC roots are considered.</summary>
    public bool UseDefaultDiscoveryRoots { get; init; } = true;

    /// <summary>Gets exact executable product versions accepted by the metadata decoder.</summary>
    public IReadOnlySet<string> SupportedProductVersions { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "11.18.0.7", "11.19.0.10" };

    /// <summary>Gets the maximum stored DVPL payload size.</summary>
    public int MaxDvplStoredBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Gets the maximum decompressed DVPL payload size.</summary>
    public int MaxDvplOutputBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Gets the maximum XML or YAML characters parsed from one resource.</summary>
    public long MaxMetadataCharacters { get; init; } = 32 * 1024 * 1024;

    /// <summary>Gets the maximum number of metadata snapshots retained in memory.</summary>
    public int MaxMetadataCacheEntries { get; init; } = 8;

    /// <summary>Gets the periodic reconciliation interval used alongside FileSystemWatcher.</summary>
    public TimeSpan LogReconciliationInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the maximum bytes scanned from the tail of an existing native log.</summary>
    public int MaxInitialLogScanBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets the maximum bytes consumed from one native log per reconciliation.</summary>
    public int MaxLogReadBytesPerPass { get; init; } = 1024 * 1024;

    /// <summary>Gets the maximum characters accepted in one native log line.</summary>
    public int MaxLogLineCharacters { get; init; } = 16 * 1024;

    /// <summary>Gets the maximum number of native log files tracked concurrently.</summary>
    public int MaxTrackedLogFiles { get; init; } = 16;

    /// <summary>Gets the bounded lifecycle event channel capacity.</summary>
    public int LogEventChannelCapacity { get; init; } = 256;

    /// <summary>
    /// Gets the private directory used for collision-safe managed replay launch
    /// copies. A null value keeps replay staging unavailable.
    /// </summary>
    public string? ReplayLaunchStagingRoot { get; init; }

    /// <summary>Gets the maximum size of one replay copied for a managed launch.</summary>
    public long MaxReplayLaunchBytes { get; init; } = 512L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxDvplStoredBytes <= 0 ||
            MaxDvplOutputBytes <= 0 ||
            MaxMetadataCharacters <= 0 ||
            MaxMetadataCacheEntries <= 0 ||
            MaxInitialLogScanBytes <= 0 ||
            MaxLogReadBytesPerPass <= 0 ||
            MaxLogLineCharacters <= 0 ||
            MaxTrackedLogFiles <= 0 ||
            LogEventChannelCapacity <= 0 ||
            MaxReplayLaunchBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GameIntegrationOptions),
                "All parser and monitor limits must be positive.");
        }

        if (MaxDvplStoredBytes > MaximumStoredDvplBytes ||
            MaxDvplOutputBytes > MaximumOutputDvplBytes ||
            MaxMetadataCharacters > MaximumMetadataCharacters ||
            MaxMetadataCacheEntries > MaximumMetadataCacheEntries ||
            MaxInitialLogScanBytes > MaximumInitialLogScanBytes ||
            MaxLogReadBytesPerPass > MaximumLogReadBytesPerPass ||
            MaxLogLineCharacters > MaximumLogLineCharacters ||
            MaxTrackedLogFiles > MaximumTrackedLogFiles ||
            LogEventChannelCapacity > MaximumLogEventChannelCapacity ||
            MaxReplayLaunchBytes > MaximumReplayLaunchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GameIntegrationOptions));
        }

        if (LogReconciliationInterval < TimeSpan.FromMilliseconds(250) ||
            LogReconciliationInterval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(GameIntegrationOptions),
                "The log reconciliation interval must be between 250 ms and 5 minutes.");
        }

        if (SupportedProductVersions.Count == 0 ||
            SupportedProductVersions.Count > MaximumSupportedVersions ||
            SupportedProductVersions.Any(
                version => string.IsNullOrWhiteSpace(version) || version.Length > 64))
        {
            throw new ArgumentException(
                "At least one non-empty exact product version must be configured.",
                nameof(GameIntegrationOptions));
        }

        if (GameInstallRoots.Count > MaximumConfiguredRoots ||
            UserDataRoots.Count > MaximumConfiguredRoots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GameIntegrationOptions),
                "Too many discovery roots were configured.");
        }

        if (ReplayLaunchStagingRoot is { Length: > 32_768 } ||
            ReplayLaunchStagingRoot is not null &&
            string.IsNullOrWhiteSpace(ReplayLaunchStagingRoot))
        {
            throw new ArgumentException(
                "The replay launch staging root is invalid.",
                nameof(GameIntegrationOptions));
        }
    }
}
