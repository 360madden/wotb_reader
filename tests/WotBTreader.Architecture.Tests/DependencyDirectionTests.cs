using System.Reflection;
using WotBTreader.Application.Replay;
using WotBTreader.CaptureLogs;
using WotBTreader.Core;
using WotBTreader.GameIntegration;
using WotBTreader.Replays;
using WotBTreader.Storage.Sqlite;

namespace WotBTreader.Architecture.Tests;

[TestClass]
public sealed class DependencyDirectionTests
{
    private static readonly string[] AllowedApplicationReferences = ["WotBTreader.Core"];

    [TestMethod]
    public void Core_HasNoProjectDependencies()
    {
        Assembly core = typeof(SourceArtifact).Assembly;

        string[] projectReferences = GetWotBTreaderReferences(core);

        Assert.HasCount(0, projectReferences);
    }

    [TestMethod]
    public void Application_DependsOnlyOnCoreProject()
    {
        Assembly application = typeof(IReplayDecoder).Assembly;

        string[] projectReferences = GetWotBTreaderReferences(application);

        CollectionAssert.AreEquivalent(
            AllowedApplicationReferences,
            projectReferences);
    }

    [TestMethod]
    public void Adapters_DoNotReferenceEachOther()
    {
        Assembly[] adapters =
        [
            typeof(CaptureLogsAssemblyMarker).Assembly,
            typeof(GameIntegrationAssemblyMarker).Assembly,
            typeof(WotbReplayDecoder).Assembly,
            typeof(SqliteStorageOptions).Assembly,
        ];

        string[] adapterNames = adapters.Select(static assembly => assembly.GetName().Name!).ToArray();
        foreach (Assembly adapter in adapters)
        {
            string[] references = GetWotBTreaderReferences(adapter);
            string[] illegal = references
                .Where(reference => adapterNames.Contains(reference, StringComparer.Ordinal) &&
                                    !string.Equals(reference, adapter.GetName().Name, StringComparison.Ordinal))
                .ToArray();

            Assert.HasCount(0, illegal, $"{adapter.GetName().Name} references another adapter.");
        }
    }

    private static string[] GetWotBTreaderReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .Where(static name => name is not null &&
                                  name.StartsWith("WotBTreader.", StringComparison.Ordinal))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
}
