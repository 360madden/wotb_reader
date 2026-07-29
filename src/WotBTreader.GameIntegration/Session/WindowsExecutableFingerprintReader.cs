using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Immutable identity collected from one read-only executable handle.
/// The file identifier is intentionally internal and is never logged.
/// </summary>
internal sealed record WindowsExecutableFingerprint(
    string CanonicalPath,
    ExecutableFileIdentity FileIdentity,
    string ProductVersion,
    ContentHash Sha256);

/// <summary>Reads a Windows executable identity while denying replacement during hashing.</summary>
internal interface IWindowsExecutableFingerprintReader
{
    ValueTask<WindowsExecutableFingerprint?> ReadAsync(
        string executablePath,
        CancellationToken cancellationToken);
}

internal sealed class WindowsExecutableFingerprintReader
    : IWindowsExecutableFingerprintReader
{
    private const int MaximumPathCharacters = 32_768;

    public async ValueTask<WindowsExecutableFingerprint?> ReadAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            SafeFileHandle fileHandle = File.OpenHandle(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                string canonicalPath = QueryFinalPath(fileHandle);
                if (string.IsNullOrWhiteSpace(canonicalPath)
                    || !NativeMethods.GetFileInformationByHandle(
                        fileHandle,
                        out NativeFileInformation fileInformation))
                {
                    return null;
                }

                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(canonicalPath);
                string? productVersion = string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                    ? versionInfo.FileVersion
                    : versionInfo.ProductVersion;
                if (string.IsNullOrWhiteSpace(productVersion))
                {
                    return null;
                }

                await using FileStream stream = new(
                    fileHandle,
                    FileAccess.Read,
                    bufferSize: 128 * 1024,
                    isAsync: true);
                fileHandle = null!;
                byte[] sha256 = await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (sha256.Length == 0
                    || !NativeMethods.GetFileInformationByHandle(
                        stream.SafeFileHandle,
                        out NativeFileInformation revalidatedFileInformation)
                    || !QueryFinalPath(stream.SafeFileHandle).Equals(
                        canonicalPath,
                        StringComparison.Ordinal)
                    || revalidatedFileInformation.VolumeSerialNumber
                        != fileInformation.VolumeSerialNumber
                    || revalidatedFileInformation.FileIndex != fileInformation.FileIndex)
                {
                    return null;
                }

                return new WindowsExecutableFingerprint(
                    canonicalPath,
                    new ExecutableFileIdentity(
                        fileInformation.VolumeSerialNumber,
                        fileInformation.FileIndex),
                    productVersion.Trim(),
                    new ContentHash(Convert.ToHexString(sha256)));
            }
            finally
            {
                fileHandle?.Dispose();
            }
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or UnauthorizedAccessException
                or IOException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException
                or Win32Exception)
        {
            return null;
        }
    }

    private static string QueryFinalPath(SafeFileHandle fileHandle)
    {
        char[] buffer = new char[MaximumPathCharacters];
        uint characters = NativeMethods.GetFinalPathNameByHandleW(
            fileHandle,
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
}
