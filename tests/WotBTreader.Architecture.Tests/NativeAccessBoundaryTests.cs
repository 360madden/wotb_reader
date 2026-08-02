using System.Reflection;
using WotBTreader.Application.Game;

namespace WotBTreader.Architecture.Tests;

[TestClass]
public sealed class NativeAccessBoundaryTests
{
    private static readonly string[] ForbiddenHostSourceTerms =
    [
        "DllImport",
        "LibraryImport",
        "OpenProcess(",
        "ReadProcessMemory(",
        "WriteProcessMemory(",
        "VirtualQueryEx(",
        "class GameMemoryReader",
    ];
    private static readonly string[] ForbiddenGameIntegrationMemoryTerms =
    [
        "PROCESS_VM_READ",
        "PROCESS_VM_WRITE",
        "PROCESS_VM_OPERATION",
        "PROCESS_ALL_ACCESS",
        "ReadProcessMemory(",
        "WriteProcessMemory(",
    ];

    [TestMethod]
    public void HostWeb_HasNoNativeInteropOrDirectMemoryReader()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "src",
            "WotBTreader.Host.Web");
        string[] violations = FindForbiddenSourceTerms(sourceRoot);

        Assert.HasCount(
            0,
            violations,
            $"Host.Web native-access violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void GameHarness_HasNoNativeInteropOrDirectMemoryReader()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "tools",
            "src",
            "WotBTreader.GameHarness");
        string[] violations = FindForbiddenSourceTerms(sourceRoot);

        Assert.HasCount(
            0,
            violations,
            $"GameHarness native-access violations: {string.Join("; ", violations)}");
    }

    private static readonly string[] AllowedGameIntegrationMemoryFiles =
    [
        // The M2 guarded reader and dynamic offset scanners moved to the
        // standalone ultimate-scanner module (see UltimateScanner test below).
        // WindowsGameProcessQueryPlatform no longer hosts VM-read P/Invokes;
        // its NativeMethods now covers launch/query/file interop only.
    ];

    [TestMethod]
    public void GameIntegration_HasNoMemoryCapableNativeAccess()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "src",
            "WotBTreader.GameIntegration");
        string[] violations = FindForbiddenSourceTerms(
            sourceRoot,
            ForbiddenGameIntegrationMemoryTerms,
            AllowedGameIntegrationMemoryFiles);

        Assert.HasCount(
            0,
            violations,
            $"GameIntegration memory-access violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void UltimateScanner_HasOnlySanctionedMemoryAccess()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "ultimate-scanner");
        string[] violations = FindForbiddenSourceTerms(
            sourceRoot,
            ForbiddenGameIntegrationMemoryTerms,
            AllowedUltimateScannerMemoryFiles);

        Assert.HasCount(
            0,
            violations,
            $"UltimateScanner memory-access violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void UltimateScanner_DiagnosticsDoNotIncludeFullExecutablePaths()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "ultimate-scanner");
        string[] violations =
        [
            .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains(
                    "executablePath={ExecutablePath}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(sourceRoot, path)),
        ];

        Assert.HasCount(
            0,
            violations,
            $"UltimateScanner full-path diagnostic violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void UltimateScanner_DiagnosticsDoNotIncludeMemorySamplesOrAddresses()
    {
        string sourceRoot = Path.Combine(
            ProjectCatalog.RepositoryRoot(),
            "ultimate-scanner");
        string[] forbiddenTerms =
        [
            "candidateSample",
            "expectedHex",
            "toleranceMaskHex",
            "baseAddress=0x",
            "regionBaseAddress=0x",
        ];
        string[] violations =
        [
            .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .SelectMany(path => forbiddenTerms
                    .Where(term => File.ReadAllText(path).Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(term =>
                        $"{Path.GetRelativePath(sourceRoot, path)} contains {term}.")),
        ];

        Assert.HasCount(
            0,
            violations,
            $"UltimateScanner sensitive diagnostic violations: {string.Join("; ", violations)}");
    }

    private static readonly string[] AllowedUltimateScannerMemoryFiles =
    [
        // The scanner module is the sanctioned VM-read surface: interop plus
        // the guarded reader, pattern scanner, and multi-scan engine.
        "GuardedMemoryReader.cs",
        "NativeMethods.cs",
        "MemoryScanDiscoverer.cs",
        "MemoryScanEngine.cs",
    ];

    [TestMethod]
    public void GameSessionContracts_ExposeNoAuthorizationOrProcessPrimitives()
    {
        Type[] contractTypes =
        [
            typeof(IGameSessionState),
            typeof(GameSessionSnapshot),
            typeof(IGameReplayLauncher),
            typeof(GameReplayLaunchRequest),
            typeof(GameReplayLaunchOutcome),
            typeof(IGameMemoryObserver),
            typeof(GameMemoryObservation),
        ];
        string[] forbiddenNames =
        [
            "Attach",
            "Lease",
            "Token",
            "ProcessId",
            "WindowHandle",
            "ExecutablePath",
            "Offset",
        ];

        string[] violations =
        [
            .. contractTypes.SelectMany(type =>
                type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(member => forbiddenNames.Any(name =>
                        member.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    .Select(member => $"{type.Name}.{member.Name}")),
            .. contractTypes.SelectMany(type =>
                type.GetMethods()
                    .SelectMany(static method =>
                        method.GetParameters().Select(static parameter => parameter.ParameterType)
                            .Append(method.ReturnType))
                    .Where(static memberType =>
                        memberType == typeof(nint)
                        || memberType.FullName?.Contains("SafeHandle", StringComparison.Ordinal) == true)
                    .Select(memberType => $"{type.Name} exposes {memberType.Name}")),
        ];

        Assert.HasCount(
            0,
            violations,
            $"Game-session contract authority leaks: {string.Join("; ", violations)}");
    }

    private static string[] FindForbiddenSourceTerms(string sourceRoot) =>
        FindForbiddenSourceTerms(sourceRoot, ForbiddenHostSourceTerms);

    private static string[] FindForbiddenSourceTerms(
        string sourceRoot,
        IReadOnlyList<string> forbiddenTerms,
        IReadOnlyList<string>? allowedFiles = null) =>
    [
        .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => allowedFiles is null
                || !allowedFiles.Any(allowed =>
                    path.EndsWith(allowed, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(path => forbiddenTerms
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term =>
                    $"{Path.GetRelativePath(sourceRoot, path)} contains {term}.")),
    ];
}
