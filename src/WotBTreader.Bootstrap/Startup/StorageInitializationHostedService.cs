using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;

namespace WotBTreader.Bootstrap.Startup;

/// <summary>
/// Applies storage migrations before any host begins serving. Failure is fatal:
/// a host that cannot prove its schema version must not accept work that would
/// write decode evidence against an unknown layout.
/// </summary>
internal sealed class StorageInitializationHostedService(
    IStorageInitializer initializer,
    ILogger<StorageInitializationHostedService> logger) : IHostedService
{
    private static readonly EventId InitializedEvent = new(4200, "StorageInitialized");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        OperationResult<int> result = await initializer
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            ApplicationError error = result.Error ??
                new ApplicationError("storage.initialize_failed", "Storage could not be initialized.");
            throw new InvalidOperationException(
                $"Storage initialization failed ({error.Code}): {error.Message}");
        }

        // The schema version is safe to log; the database path is not.
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                InitializedEvent,
                "Storage initialized at schema version {SchemaVersion}.",
                result.Value);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
