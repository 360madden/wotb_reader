using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WotBTreader.GameIntegration.Session;

/// <summary>Resolves the trusted executable module base for an already authorized process.</summary>
internal interface IGameProcessModuleBaseAddressResolver
{
    nint Resolve(int processId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the main module base on demand. Failure is represented by zero so
/// callers can fail closed and retry after a transient process/module race.
/// </summary>
internal sealed class WindowsGameProcessModuleBaseAddressResolver : IGameProcessModuleBaseAddressResolver
{
    private readonly ILogger<WindowsGameProcessModuleBaseAddressResolver> _logger;

    public WindowsGameProcessModuleBaseAddressResolver(
        ILogger<WindowsGameProcessModuleBaseAddressResolver> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public nint Resolve(int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return nint.Zero;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            nint baseAddress = process.MainModule?.BaseAddress ?? nint.Zero;
            cancellationToken.ThrowIfCancellationRequested();
            if (baseAddress == nint.Zero)
            {
                _logger.LogWarning(
                    new EventId(3160, "ModuleBaseAddressUnresolved"),
                    "Module base resolved to zero for processId={ProcessId}; the next poll retries fail-closed.",
                    processId);
            }

            return baseAddress;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                new EventId(3161, "ModuleBaseAddressResolveFailed"),
                exception,
                "Module base resolution failed for processId={ProcessId}; returning zero (fail closed).",
                processId);
            return nint.Zero;
        }
    }
}
