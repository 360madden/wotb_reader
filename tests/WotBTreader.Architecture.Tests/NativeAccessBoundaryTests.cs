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
    [
        .. Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => ForbiddenHostSourceTerms
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.Ordinal))
                .Select(term =>
                    $"{Path.GetRelativePath(sourceRoot, path)} contains {term}.")),
    ];
}
