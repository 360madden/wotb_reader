using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WotBTreader.Application.Results;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.DependencyInjection;
using WotBTreader.Bootstrap.Logging;

// Unqualified 'Host' binds to the WotBTreader.Host namespace inside this project.
using HostingHost = Microsoft.Extensions.Hosting.Host;

namespace WotBTreader.Host.Cli.Cli;

/// <summary>
/// Runs one CLI command against a fully composed host. Kept separate from
/// <c>Program</c> so the whole invocation path is exercised in-process by tests.
/// </summary>
public static class CliEntryPoint
{
    /// <summary>
    /// Parses arguments, builds a fully composed host (running storage migrations
    /// at startup), dispatches the command through <see cref="CliCommandRouter"/>,
    /// and writes the machine-readable envelope to the appropriate output stream.
    /// </summary>
    /// <remarks>
    /// JSON envelopes always go to <paramref name="standardOutput"/> so a failed
    /// command stays pipeable. Human-readable error text goes to
    /// <paramref name="standardError"/>. The catch-all handler surfaces only the
    /// exception type name, never its message, because exception text routinely
    /// embeds local paths.
    /// </remarks>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        Guid correlationId = Guid.CreateVersion7();
        OperationResult<CliInvocation> parsed = CliInvocation.Parse(arguments);
        if (!parsed.IsSuccess || parsed.Value is null)
        {
            ApplicationError parseError = parsed.Error ??
                new ApplicationError("cli.command.required", "A command is required.");
            return await WriteAsync(
                Failure(CliExitCode.InvalidArguments, parseError.Code, parseError.Message, correlationId),
                json: false,
                standardOutput,
                standardError,
                cancellationToken).ConfigureAwait(false);
        }

        CliInvocation invocation = parsed.Value;
        try
        {
            using IHost host = BuildHost(invocation);
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using IServiceScope scope = host.Services.CreateScope();
                CliCommandRouter router = scope.ServiceProvider.GetRequiredService<CliCommandRouter>();
                CliExecution execution = await router
                    .ExecuteAsync(invocation, correlationId, cancellationToken)
                    .ConfigureAwait(false);
                return await WriteAsync(
                    execution,
                    invocation.Json,
                    standardOutput,
                    standardError,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Shutdown must still run when the caller's token is already cancelled.
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return await WriteAsync(
                Failure(
                    CliExitCode.Cancelled,
                    "cli.cancelled",
                    "The operation was cancelled before it completed.",
                    correlationId),
                invocation.Json,
                standardOutput,
                standardError,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Exception text routinely embeds local paths, so only the stable type
            // name reaches the console. Full detail stays in the local log sink.
            return await WriteAsync(
                Failure(
                    CliExitCode.InternalFailure,
                    "cli.internal_failure",
                    $"An unexpected {exception.GetType().Name} occurred. See the local application log for details.",
                    correlationId),
                invocation.Json,
                standardOutput,
                standardError,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IHost BuildHost(CliInvocation invocation)
    {
        string? dataRoot =
            invocation.Options.TryGetValue("data-root", out string? configured) &&
            !string.IsNullOrWhiteSpace(configured)
                ? configured
                : null;

        // An empty builder is deliberate: default host logging writes to stdout,
        // which would corrupt the machine-readable envelope. The Serilog console
        // sink configured below emits every level to stderr instead.
        HostApplicationBuilder builder = HostingHost.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ApplicationName = "wotbtreader",
            });

        LocalApplicationPaths paths = LocalApplicationPaths.Create(dataRoot);
        paths.EnsureDirectoriesExist();
        builder.AddTreaderLogging(paths, "cli");
        // The CLI runs a single command per process, so connection pooling buys
        // nothing and only leaves file handles open until the process exits. It
        // also forces tests that reuse the process to clear the global pool, which
        // races with parallel invocations. Non-pooled connections close on dispose.
        builder.Services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(
            ApplicationDataRoot: dataRoot,
            SqliteConnectionPooling: false));
        builder.Services.AddScoped<CliCommandRouter>();
        return builder.Build();
    }

    private static async ValueTask<int> WriteAsync(
        CliExecution execution,
        bool json,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        // Machine output always goes to stdout so a failed command stays pipeable;
        // human-readable failures go to stderr.
        TextWriter writer = json || execution.ExitCode == CliExitCode.Success
            ? standardOutput
            : standardError;
        await CliOutput.WriteAsync(execution, json, writer, cancellationToken).ConfigureAwait(false);
        return (int)execution.ExitCode;
    }

    private static CliExecution Failure(
        CliExitCode exitCode,
        string code,
        string message,
        Guid correlationId) =>
        new(
            exitCode,
            new CliEnvelope(
                "1",
                Success: false,
                correlationId,
                Data: null,
                Warnings: [],
                [new CliError(code, message, Retryable: false)]),
            message);
}
