using System.IO.Compression;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.Diagnostics;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class DiagnosticBundleServiceTests
{
    private static readonly string[] ExpectedDefaultEntries = ["doctor.json", "manifest.json"];

    [TestMethod]
    public async Task DefaultBundleContainsOnlyRedactedMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wotbtreader-diagnostics-{Guid.CreateVersion7():N}");
        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);
        paths.EnsureDirectoriesExist();
        try
        {
            DiagnosticBundleService service = new(paths, new StubDoctor(), TimeProvider.System);

            var result = await service.CreateAsync(
                new DiagnosticBundleOptions(
                    IncludeDatabase: false,
                    IncludeSourceArtifacts: false,
                    IncludeScreenshots: false),
                CancellationToken.None);

            Assert.IsTrue(result.IsSuccess);
            Assert.IsNotNull(result.Value);
            using ZipArchive archive = ZipFile.OpenRead(result.Value);
            CollectionAssert.AreEquivalent(
                ExpectedDefaultEntries,
                archive.Entries.Select(static entry => entry.FullName).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SensitiveArtifactsRequireDedicatedProvider()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wotbtreader-diagnostics-{Guid.CreateVersion7():N}");
        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);
        paths.EnsureDirectoriesExist();
        try
        {
            DiagnosticBundleService service = new(paths, new StubDoctor(), TimeProvider.System);

            var result = await service.CreateAsync(
                new DiagnosticBundleOptions(
                    IncludeDatabase: false,
                    IncludeSourceArtifacts: true,
                    IncludeScreenshots: false),
                CancellationToken.None);

            Assert.AreEqual("diagnostics.sensitive_export.unsupported", result.Error?.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StubDoctor : IDoctorService
    {
        public ValueTask<DoctorReport> RunAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DoctorReport(
                "1",
                DateTimeOffset.UnixEpoch,
                [
                    new DiagnosticCheck(
                        "test",
                        "pass",
                        "Synthetic check.",
                        Required: true,
                        new Dictionary<string, string>()),
                ]));
    }
}
