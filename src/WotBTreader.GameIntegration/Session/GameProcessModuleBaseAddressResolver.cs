using System.Diagnostics;

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
            return baseAddress;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return nint.Zero;
        }
    }
}
