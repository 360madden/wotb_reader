using System.Runtime.InteropServices;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Diagnostics;

public sealed class DoctorService : IDoctorService
{
    private readonly LocalApplicationPaths _paths;
    private readonly IReplayDecoder[] _decoders;
    private readonly IInstalledGameMetadataProvider[] _metadataProviders;
    private readonly TimeProvider _timeProvider;

    public DoctorService(
        LocalApplicationPaths paths,
        IEnumerable<IReplayDecoder> decoders,
        IEnumerable<IInstalledGameMetadataProvider> metadataProviders,
        TimeProvider timeProvider)
    {
        _paths = paths;
        _decoders = decoders.ToArray();
        _metadataProviders = metadataProviders.ToArray();
        _timeProvider = timeProvider;
    }

    public async ValueTask<DoctorReport> RunAsync(CancellationToken cancellationToken)
    {
        List<DiagnosticCheck> checks =
        [
            new(
                "runtime",
                Environment.Version.Major == 10 ? "pass" : "fail",
                $".NET runtime major version is {Environment.Version.Major}.",
                Required: true,
                new Dictionary<string, string>
                {
                    ["framework"] = RuntimeInformation.FrameworkDescription,
                    ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                }),
            new(
                "operating-system",
                OperatingSystem.IsWindows() ? "pass" : "fail",
                OperatingSystem.IsWindows()
                    ? "Windows host detected."
                    : "The alpha overlay and game integration require Windows.",
                Required: true,
                new Dictionary<string, string>
                {
                    ["description"] = RuntimeInformation.OSDescription,
                }),
            new(
                "application-data",
                RequiredDirectoriesExist() ? "pass" : "fail",
                RequiredDirectoriesExist()
                    ? "Application data directories are initialized."
                    : "One or more application data directories are unavailable.",
                Required: true,
                new Dictionary<string, string>()),
            new(
                "replay-decoders",
                _decoders.Length > 0 ? "pass" : "fail",
                $"{_decoders.Length} replay decoder(s) registered.",
                Required: true,
                _decoders.ToDictionary(
                    static decoder => decoder.Descriptor.Id,
                    static decoder => decoder.Descriptor.Version,
                    StringComparer.Ordinal)),
        ];

        if (_metadataProviders.Length == 0)
        {
            checks.Add(new DiagnosticCheck(
                "installed-game-metadata",
                "warn",
                "No installed-game metadata provider is registered.",
                Required: false,
                new Dictionary<string, string>()));
        }
        else
        {
            foreach (IInstalledGameMetadataProvider provider in _metadataProviders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
                checks.Add(new DiagnosticCheck(
                    "installed-game-metadata",
                    result.IsSuccess ? "pass" : "warn",
                    result.IsSuccess
                        ? "A version-gated local game installation is available."
                        : result.Error?.Message ?? "Installed-game metadata is unavailable.",
                    Required: false,
                    result.Value is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>
                        {
                            ["gameVersion"] = result.Value.Identity.ProductVersion,
                            ["providerVersion"] = result.Value.ProviderVersion,
                        }));
            }
        }

        return new DoctorReport(
            SchemaVersion: "1",
            _timeProvider.GetUtcNow(),
            checks);
    }

    private bool RequiredDirectoriesExist() =>
        Directory.Exists(_paths.Root) &&
        Directory.Exists(_paths.ContentStore) &&
        Directory.Exists(_paths.Logs) &&
        Directory.Exists(_paths.Diagnostics) &&
        Directory.Exists(_paths.Rendezvous);
}
