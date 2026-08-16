using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Host.Web.Infrastructure;

namespace WotBTreader.Host.Web.Services;

internal sealed class RendezvousOptions
{
    public const string SectionName = "Rendezvous";

    public string FileName { get; init; } = "web.json";

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(2);
}

internal sealed class RendezvousPublisher(
    IHostApplicationLifetime lifetime,
    IServer server,
    LocalApplicationPaths paths,
    LocalMutationSecurity security,
    IOptions<RendezvousOptions> options,
    TimeProvider timeProvider,
    ILogger<RendezvousPublisher> logger) : BackgroundService
{
    private static readonly EventId PublishedEvent = new(4100, "RendezvousPublished");
    private static readonly EventId PublishFailedEvent = new(4101, "RendezvousPublishFailed");
    private static readonly EventId RemovedEvent = new(4102, "RendezvousRemoved");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Guid instanceId = Guid.CreateVersion7();
    private readonly string rendezvousFile = ResolveFile(paths, options.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForServerStartAsync(lifetime, stoppingToken);

        var interval = options.Value.RefreshInterval;
        if (interval < TimeSpan.FromSeconds(15))
        {
            interval = TimeSpan.FromSeconds(15);
        }

        using var timer = new PeriodicTimer(interval, timeProvider);
        do
        {
            try
            {
                await PublishAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException)
            {
                // The rendezvous record is a discoverability aid. Failure must not
                // take down a dashboard that is already safely bound to loopback.
                logger.LogError(
                    PublishFailedEvent,
                    exception,
                    "Could not publish the local web rendezvous record.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            if (!File.Exists(rendezvousFile))
            {
                return;
            }

            await using var stream = new FileStream(
                rendezvousFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var record = await JsonSerializer.DeserializeAsync<RendezvousRecord>(
                stream,
                JsonOptions,
                cancellationToken);
            stream.Close();

            if (record?.InstanceId == instanceId)
            {
                File.Delete(rendezvousFile);
                logger.LogInformation(RemovedEvent, "Removed the local web rendezvous record.");
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The rendezvous directory was removed between the File.Exists
            // check and the FileStream open — benign race during shutdown.
        }
        catch (FileNotFoundException)
        {
            // The file was deleted between the File.Exists check and open.
        }
        catch (IOException)
        {
            // A stale record expires quickly and is rejected by every client.
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        var address = ResolveLoopbackAddress(server);
        var lease = security.Rotate();
        var record = new RendezvousRecord(
            SchemaVersion: "1.0",
            InstanceId: instanceId,
            ProcessId: Environment.ProcessId,
            BaseUri: address,
            Capability: lease.Token,
            IssuedAtUtc: timeProvider.GetUtcNow(),
            ExpiresAtUtc: lease.ExpiresAtUtc);

        var directory = Path.GetDirectoryName(rendezvousFile)
            ?? throw new InvalidOperationException("Rendezvous path has no parent directory.");

        // Re-securing rather than plain creation: the capability written below
        // must never land in a directory that inherited a permissive parent ACL.
        paths.EnsureRendezvousDirectory();

        var temporaryFile = Path.Combine(
            directory,
            $".{Path.GetFileName(rendezvousFile)}.{Guid.CreateVersion7():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    record,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // The directory's owner-only ACL protects this file only through
            // inheritance. Pin an explicit, protected owner-only descriptor
            // onto the temporary file before it becomes the published record
            // so the capability never ships under a merely-inherited ACL.
            LocalApplicationPaths.ProtectRendezvousFile(temporaryFile);

            File.Move(temporaryFile, rendezvousFile, overwrite: true);

            // After the rename, positively re-verify the final record: it must
            // still be a real file (not a reparse point) with a protected,
            // current-user-only DACL. A failure propagates and the publisher
            // rotates the lease again on the next cycle.
            LocalApplicationPaths.VerifyRendezvousFile(rendezvousFile);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }

        // Token, full path, and user profile location are intentionally excluded.
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                PublishedEvent,
                "Published local web rendezvous metadata expiring at {ExpiresAtUtc}.",
                lease.ExpiresAtUtc);
        }
    }

    private static async Task WaitForServerStartAsync(
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        await completion.Task.WaitAsync(cancellationToken);
    }

    private static Uri ResolveLoopbackAddress(IServer server)
    {
        var addresses = server.Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(uri =>
                uri is not null &&
                LoopbackOnlyMiddleware.IsLoopbackHost(uri.Host));

        return address ??
            throw new InvalidOperationException("No active loopback listener was found.");
    }

    private static string ResolveFile(
        LocalApplicationPaths paths,
        RendezvousOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FileName) ||
            Path.IsPathRooted(options.FileName) ||
            options.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "Rendezvous:FileName must be a simple file name.");
        }

        return Path.Combine(paths.Rendezvous, options.FileName);
    }

    private sealed record RendezvousRecord(
        string SchemaVersion,
        Guid InstanceId,
        int ProcessId,
        Uri BaseUri,
        string Capability,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc);
}
