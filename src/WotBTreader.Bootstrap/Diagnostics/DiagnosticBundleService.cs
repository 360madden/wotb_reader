using System.IO.Compression;
using System.Text.Json;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Diagnostics;

public sealed class DiagnosticBundleService : IDiagnosticBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly LocalApplicationPaths _paths;
    private readonly IDoctorService _doctor;
    private readonly TimeProvider _timeProvider;

    public DiagnosticBundleService(
        LocalApplicationPaths paths,
        IDoctorService doctor,
        TimeProvider timeProvider)
    {
        _paths = paths;
        _doctor = doctor;
        _timeProvider = timeProvider;
    }

    public async ValueTask<OperationResult<string>> CreateAsync(
        DiagnosticBundleOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IncludeSourceArtifacts || options.IncludeScreenshots)
        {
            return OperationResult.Failure<string>(new ApplicationError(
                "diagnostics.sensitive_export.unsupported",
                "Source artifacts and screenshots require a future explicit sensitive-export provider."));
        }

        string bundleId = Guid.CreateVersion7().ToString("N");
        string finalPath = Path.Combine(_paths.Diagnostics, $"diagnostics-{bundleId}.zip");
        string temporaryPath = finalPath + ".partial";

        try
        {
            DoctorReport report = await _doctor.RunAsync(cancellationToken).ConfigureAwait(false);
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using (ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // ZipArchiveMode.Create writes entries strictly sequentially, so each
                    // entry stream must be closed before the next entry is created.
                    ZipArchiveEntry doctorEntry = archive.CreateEntry("doctor.json", CompressionLevel.SmallestSize);
                    await using (Stream doctorStream = doctorEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            doctorStream,
                            report,
                            JsonOptions,
                            cancellationToken).ConfigureAwait(false);
                    }

                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
                    await using (Stream manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            new
                            {
                                schemaVersion = "1",
                                createdAtUtc = _timeProvider.GetUtcNow(),
                                includesDatabase = options.IncludeDatabase,
                                includesSourceArtifacts = false,
                                includesScreenshots = false,
                            },
                            JsonOptions,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The stream must be fully closed before the atomic publish rename.
            File.Move(temporaryPath, finalPath);
            return OperationResult.Success(finalPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeletePartial(temporaryPath);
            return OperationResult.Failure<string>(
                new ApplicationError("operation.cancelled", "Diagnostic export was cancelled."));
        }
        catch (IOException)
        {
            TryDeletePartial(temporaryPath);
            return OperationResult.Failure<string>(
                new ApplicationError("diagnostics.write_failed", "Diagnostic bundle could not be written."));
        }
        catch (UnauthorizedAccessException)
        {
            TryDeletePartial(temporaryPath);
            return OperationResult.Failure<string>(
                new ApplicationError("diagnostics.access_denied", "Diagnostic bundle destination is unavailable."));
        }
    }

    private static void TryDeletePartial(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup. The ignored .partial file is recoverable and never replaces a valid bundle.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup. The caller already receives a stable failure result.
        }
    }
}
