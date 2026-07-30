using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Pins the trusted game executable and its containing canonical directory for a later native
/// launch. Acquiring this lease deliberately does not create, resume, or inspect a process.
/// </summary>
internal sealed class WindowsTrustedExecutableLaunchLease : IAsyncDisposable
{
    private const int MaximumPathCharacters = 32_768;
    private const int HashBufferSize = 128 * 1024;

    private SafeFileHandle? _directoryHandle;
    private SafeFileHandle? _executableHandle;

    private WindowsTrustedExecutableLaunchLease(
        string canonicalExecutablePath,
        TrustedGameExecutableIdentity executableIdentity,
        SafeFileHandle directoryHandle,
        SafeFileHandle executableHandle)
    {
        CanonicalExecutablePath = canonicalExecutablePath;
        ExecutableIdentity = executableIdentity;
        _directoryHandle = directoryHandle;
        _executableHandle = executableHandle;
    }

    internal string CanonicalExecutablePath { get; }

    internal TrustedGameExecutableIdentity ExecutableIdentity { get; }

    internal SafeFileHandle ExecutableHandle => _executableHandle!;

    internal static async ValueTask<OperationResult<WindowsTrustedExecutableLaunchLease>> AcquireAsync(
        TrustedGameExecutableIdentity executableIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executableIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Failure();
        }

        SafeFileHandle? directoryHandle = null;
        SafeFileHandle? executableHandle = null;
        try
        {
            string expectedExecutablePath = Path.GetFullPath(executableIdentity.Identity.ExecutablePath);
            string? expectedDirectoryPath = Path.GetDirectoryName(expectedExecutablePath);
            if (string.IsNullOrWhiteSpace(expectedDirectoryPath))
            {
                return Failure();
            }

            directoryHandle = OpenPinnedDirectory(expectedDirectoryPath);
            executableHandle = File.OpenHandle(
                expectedExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            string canonicalExecutablePath = QueryFinalPath(executableHandle);
            if (string.IsNullOrWhiteSpace(canonicalExecutablePath)
                || !NativeMethods.GetFileInformationByHandle(
                    executableHandle,
                    out NativeFileInformation initialInformation)
                || (initialInformation.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return Failure();
            }

            string? canonicalExecutableDirectory = Path.GetDirectoryName(canonicalExecutablePath);
            if (string.IsNullOrWhiteSpace(canonicalExecutableDirectory)
                || !string.Equals(
                    QueryFinalPath(directoryHandle),
                    canonicalExecutableDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure();
            }

            string? productVersion = ReadProductVersion(canonicalExecutablePath);
            if (string.IsNullOrWhiteSpace(productVersion))
            {
                return Failure();
            }

            ContentHash sha256 = await HashAsync(executableHandle, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.GetFileInformationByHandle(
                    executableHandle,
                    out NativeFileInformation revalidatedInformation)
                || (revalidatedInformation.FileAttributes & FileAttributes.ReparsePoint) != 0
                || !string.Equals(
                    QueryFinalPath(executableHandle),
                    canonicalExecutablePath,
                    StringComparison.Ordinal)
                || revalidatedInformation.VolumeSerialNumber != initialInformation.VolumeSerialNumber
                || revalidatedInformation.FileIndex != initialInformation.FileIndex
                || !MatchesExpected(
                    executableIdentity,
                    canonicalExecutablePath,
                    revalidatedInformation,
                    productVersion,
                    sha256))
            {
                return Failure();
            }

            var lease = new WindowsTrustedExecutableLaunchLease(
                canonicalExecutablePath,
                executableIdentity,
                directoryHandle,
                executableHandle);
            directoryHandle = null;
            executableHandle = null;
            return OperationResult.Success(lease);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or Win32Exception
                or CryptographicException
                or OverflowException)
        {
            return Failure();
        }
        finally
        {
            executableHandle?.Dispose();
            directoryHandle?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _executableHandle, null)?.Dispose();
        Interlocked.Exchange(ref _directoryHandle, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => nameof(WindowsTrustedExecutableLaunchLease);

    private static SafeFileHandle OpenPinnedDirectory(string expectedDirectoryPath)
    {
        SafeFileHandle handle = NativeDirectoryMethods.CreateFileW(
            expectedDirectoryPath,
            NativeDirectoryMethods.FileListDirectory,
            NativeDirectoryMethods.FileShareRead | NativeDirectoryMethods.FileShareWrite,
            nint.Zero,
            NativeDirectoryMethods.OpenExisting,
            NativeDirectoryMethods.FileFlagBackupSemantics |
            NativeDirectoryMethods.FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid
            || !NativeMethods.GetFileInformationByHandle(handle, out NativeFileInformation information)
            || (information.FileAttributes & FileAttributes.ReparsePoint) != 0
            || string.IsNullOrWhiteSpace(QueryFinalPath(handle)))
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("The trusted executable directory could not be pinned.");
        }

        return handle;
    }

    private static async ValueTask<ContentHash> HashAsync(
        SafeFileHandle executableHandle,
        CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(executableHandle);
        if (length <= 0)
        {
            throw new IOException("The trusted executable is empty.");
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(HashBufferSize);
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < length)
        {
            int requested = (int)Math.Min(buffer.Length, length - offset);
            int read = await RandomAccess
                .ReadAsync(executableHandle, buffer.AsMemory(0, requested), offset, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The trusted executable changed while being read.");
            }

            hasher.AppendData(buffer, 0, read);
            offset = checked(offset + read);
        }

        return new ContentHash(Convert.ToHexString(hasher.GetHashAndReset()));
    }

    private static string? ReadProductVersion(string canonicalExecutablePath)
    {
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(canonicalExecutablePath);
        string? value = string.IsNullOrWhiteSpace(version.ProductVersion)
            ? version.FileVersion
            : version.ProductVersion;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool MatchesExpected(
        TrustedGameExecutableIdentity expected,
        string canonicalExecutablePath,
        NativeFileInformation information,
        string productVersion,
        ContentHash sha256) =>
        string.Equals(
            expected.Identity.ExecutablePath,
            canonicalExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && information.VolumeSerialNumber == expected.FileIdentity.VolumeSerialNumber
        && information.FileIndex == expected.FileIdentity.FileIndex
        && string.Equals(expected.Identity.ProductVersion, productVersion, StringComparison.Ordinal)
        && expected.Identity.ExecutableSha256 == sha256;

    private static string QueryFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[MaximumPathCharacters];
        uint characters = NativeMethods.GetFinalPathNameByHandleW(
            handle,
            buffer,
            checked((uint)buffer.Length),
            dwFlags: 0);
        if (characters == 0 || characters >= buffer.Length)
        {
            return string.Empty;
        }

        string path = new(buffer, 0, checked((int)characters));
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static OperationResult<WindowsTrustedExecutableLaunchLease> Failure() =>
        OperationResult.Failure<WindowsTrustedExecutableLaunchLease>(
            new ApplicationError(
                "game.launch.executable_unavailable",
                "The trusted game executable is unavailable.",
                Retryable: true));

    private static class NativeDirectoryMethods
    {
        internal const uint FileListDirectory = 0x0000_0001;
        internal const uint FileShareRead = 0x0000_0001;
        internal const uint FileShareWrite = 0x0000_0002;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOpenReparsePoint = 0x0020_0000;
        internal const uint FileFlagBackupSemantics = 0x0200_0000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);
    }
}
