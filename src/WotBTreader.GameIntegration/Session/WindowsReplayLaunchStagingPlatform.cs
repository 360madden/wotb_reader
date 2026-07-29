using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.GameIntegration.Session;

/// <summary>Generates caller-independent, collision-resistant replay stage names.</summary>
internal sealed class ReplayLaunchStageNameGenerator : IReplayLaunchStageNameGenerator
{
    public string Generate() => $"{Guid.NewGuid():N}.wotbreplay";
}

/// <summary>
/// Creates launch copies in an owner-only Windows directory and keeps each
/// successful file pinned against write, delete, and replacement.
/// </summary>
internal sealed class WindowsReplayLaunchStagingPlatform
    : IReplayLaunchStagingPlatform
{
    private const int MaximumPathCharacters = 32_768;

    public ValueTask<IReplayLaunchStagingFile?> CreateNewAsync(
        string stagingRoot,
        string fileName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Managed replay staging requires Windows.");
        }

        WindowsReplayLaunchStagingFile? created =
            CreateNewWindows(stagingRoot, fileName);
        return ValueTask.FromResult<IReplayLaunchStagingFile?>(created);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsReplayLaunchStagingFile? CreateNewWindows(
        string stagingRoot,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A staging name must not contain a directory.",
                nameof(fileName));
        }

        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingRoot));
        EnsureOwnerOnlyDirectory(root);
        RejectReparseAncestry(root);
        SafeFileHandle directoryHandle = OpenPinnedDirectory(root);

        try
        {
            string path = Path.GetFullPath(Path.Combine(root, fileName));
            if (!string.Equals(
                    Path.GetDirectoryName(path),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The staging path escaped its configured directory.");
            }

            SafeFileHandle? handle = CreateOwnerOnlyFile(
                path,
                out bool collision);
            if (collision)
            {
                return null;
            }

            ArgumentNullException.ThrowIfNull(handle);
            try
            {
                if (!NativeMethods.GetFileInformationByHandle(
                        handle,
                        out NativeFileInformation information) ||
                    (information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "The staged replay identity could not be established.");
                }

                VerifyPinnedOwnerOnlySecurity(
                    handle,
                    expectedInheritanceFlags: InheritanceFlags.None);
                var identity = new ExecutableFileIdentity(
                    information.VolumeSerialNumber,
                    information.FileIndex);
                var stream = new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    bufferSize: 64 * 1024,
                    isAsync: true);
                handle = null!;
                SafeFileHandle retainedDirectoryHandle = directoryHandle;
                directoryHandle = null!;
                return new WindowsReplayLaunchStagingFile(
                    path,
                    identity,
                    stream,
                    retainedDirectoryHandle);
            }
            finally
            {
                handle?.Dispose();
            }
        }
        finally
        {
            directoryHandle?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenPinnedDirectory(string expectedPath)
    {
        SafeFileHandle handle = DeleteNativeMethods.CreateFileW(
            expectedPath,
            DeleteNativeMethods.FileListDirectory |
            DeleteNativeMethods.ReadControl,
            DeleteNativeMethods.FileShareRead |
            DeleteNativeMethods.FileShareWrite,
            lpSecurityAttributes: nint.Zero,
            DeleteNativeMethods.OpenExisting,
            DeleteNativeMethods.FileFlagBackupSemantics |
            DeleteNativeMethods.FileFlagOpenReparsePoint,
            hTemplateFile: nint.Zero);
        if (handle.IsInvalid ||
            !NativeMethods.GetFileInformationByHandle(
                handle,
                out NativeFileInformation information) ||
            (information.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
            !string.Equals(
                QueryFinalPath(handle),
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            throw new UnauthorizedAccessException(
                "The replay staging directory could not be pinned safely.");
        }

        VerifyPinnedOwnerOnlySecurity(
            handle,
            InheritanceFlags.ContainerInherit |
            InheritanceFlags.ObjectInherit);
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? CreateOwnerOnlyFile(
        string path,
        out bool collision)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");
        FileSecurity security = CreateOwnerOnlyFileSecurity(owner);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        GCHandle pinnedDescriptor = GCHandle.Alloc(
            descriptor,
            GCHandleType.Pinned);
        nint attributesPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<SecurityAttributes>());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
                InheritHandle = false,
            };
            Marshal.StructureToPtr(
                attributes,
                attributesPointer,
                fDeleteOld: false);
            SafeFileHandle handle = DeleteNativeMethods.CreateFileW(
                path,
                DeleteNativeMethods.GenericRead |
                DeleteNativeMethods.GenericWrite |
                DeleteNativeMethods.ReadControl,
                DeleteNativeMethods.FileShareRead,
                attributesPointer,
                DeleteNativeMethods.CreateNew,
                DeleteNativeMethods.FileFlagOverlapped |
                DeleteNativeMethods.FileFlagSequentialScan |
                DeleteNativeMethods.FileFlagWriteThrough,
                hTemplateFile: nint.Zero);
            if (!handle.IsInvalid)
            {
                collision = false;
                return handle;
            }

            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is DeleteNativeMethods.ErrorFileExists
                or DeleteNativeMethods.ErrorAlreadyExists)
            {
                collision = true;
                return null;
            }

            throw new Win32Exception(error);
        }
        finally
        {
            Marshal.FreeHGlobal(attributesPointer);
            pinnedDescriptor.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateOwnerOnlyFileSecurity(
        SecurityIdentifier owner)
    {
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPinnedOwnerOnlySecurity(
        SafeFileHandle handle,
        InheritanceFlags expectedInheritanceFlags)
    {
        uint requestedInformation =
            SecurityNativeMethods.OwnerSecurityInformation |
            SecurityNativeMethods.DaclSecurityInformation;
        if (!SecurityNativeMethods.GetKernelObjectSecurity(
                handle,
                requestedInformation,
                securityDescriptor: null,
                length: 0,
                out uint required) &&
            Marshal.GetLastPInvokeError() !=
                SecurityNativeMethods.ErrorInsufficientBuffer)
        {
            throw new UnauthorizedAccessException(
                "The pinned replay staging directory security is unavailable.");
        }

        if (required == 0 || required > 64 * 1024)
        {
            throw new UnauthorizedAccessException(
                "The pinned replay staging directory security is invalid.");
        }

        byte[] bytes = new byte[required];
        if (!SecurityNativeMethods.GetKernelObjectSecurity(
                handle,
                requestedInformation,
                bytes,
                checked((uint)bytes.Length),
                out _))
        {
            throw new UnauthorizedAccessException(
                "The pinned replay staging directory security is unavailable.");
        }

        var descriptor = new RawSecurityDescriptor(bytes, offset: 0);
        SecurityIdentifier expectedOwner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");
        RawAcl? dacl = descriptor.DiscretionaryAcl;
        if (descriptor.Owner is null ||
            !expectedOwner.Equals(descriptor.Owner) ||
            (descriptor.ControlFlags &
                ControlFlags.DiscretionaryAclProtected) == 0 ||
            dacl is null ||
            dacl.Count != 1 ||
            dacl[0] is not CommonAce ace ||
            ace.IsInherited ||
            ace.AceQualifier != AceQualifier.AccessAllowed ||
            !expectedOwner.Equals(ace.SecurityIdentifier) ||
            (ace.AccessMask & (int)FileSystemRights.FullControl) !=
                (int)FileSystemRights.FullControl ||
            ace.InheritanceFlags != expectedInheritanceFlags ||
            ace.PropagationFlags != PropagationFlags.None)
        {
            throw new UnauthorizedAccessException(
                "The pinned replay staging directory security is unsafe.");
        }
    }

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

    [SupportedOSPlatform("windows")]
    private static void EnsureOwnerOnlyDirectory(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");
        DirectorySecurity expected = CreateOwnerOnlySecurity(owner);
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The replay staging directory must not be a reparse point.");
            }

            directory.SetAccessControl(expected);
        }
        else
        {
            directory.Create(expected);
        }

        directory.Refresh();
        DirectorySecurity actual = directory.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !actual.AreAccessRulesProtected ||
            !owner.Equals(actual.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException(
                "The replay staging directory identity is unsafe.");
        }

        AuthorizationRuleCollection rules = actual.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        if (rules.Count != 1 ||
            rules[0] is not FileSystemAccessRule rule ||
            rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow ||
            !owner.Equals(rule.IdentityReference) ||
            (rule.FileSystemRights & FileSystemRights.FullControl) !=
                FileSystemRights.FullControl ||
            rule.InheritanceFlags !=
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
        {
            throw new UnauthorizedAccessException(
                "The replay staging directory access rules are unsafe.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateOwnerOnlySecurity(
        SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    private static void RejectReparseAncestry(string path)
    {
        for (DirectoryInfo? directory = new(path);
             directory is not null;
             directory = directory.Parent)
        {
            directory.Refresh();
            if (directory.Exists &&
                (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The replay staging directory ancestry must not contain a reparse point.");
            }
        }
    }

    private sealed class WindowsReplayLaunchStagingFile(
        string path,
        ExecutableFileIdentity identity,
        FileStream stream,
        SafeFileHandle directoryHandle)
        : IReplayLaunchStagingFile
    {
        private FileStream? _stream = stream;
        private SafeFileHandle? _directoryHandle = directoryHandle;
        private int _sealed;
        private int _disposed;

        public string Path { get; } = path;

        public Stream Stream =>
            Volatile.Read(ref _stream)
            ?? throw new ObjectDisposedException(
                nameof(WindowsReplayLaunchStagingFile));

        public ValueTask<bool> SealAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Interlocked.Exchange(ref _sealed, 1) != 0)
            {
                return ValueTask.FromResult(false);
            }

            FileStream? writable = Interlocked.Exchange(ref _stream, null);
            if (writable is null)
            {
                return ValueTask.FromResult(false);
            }

            writable.Dispose();
            SafeFileHandle? handle = null;
            try
            {
                handle = File.OpenHandle(
                    Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!NativeMethods.GetFileInformationByHandle(
                        handle,
                        out NativeFileInformation information) ||
                    (information.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
                    information.VolumeSerialNumber != identity.VolumeSerialNumber ||
                    information.FileIndex != identity.FileIndex)
                {
                    return ValueTask.FromResult(false);
                }

                var reader = new FileStream(
                    handle,
                    FileAccess.Read,
                    bufferSize: 64 * 1024,
                    isAsync: true);
                handle = null;
                _stream = reader;
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(true);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException
                    or UnauthorizedAccessException
                    or IOException)
            {
                return ValueTask.FromResult(false);
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            FileStream? owned = Interlocked.Exchange(ref _stream, null);
            bool cleanupFailed = false;
            try
            {
                try
                {
                    owned?.Dispose();
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    cleanupFailed = true;
                }

                if (!DeleteIfIdentityMatches(Path, identity))
                {
                    cleanupFailed = true;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _directoryHandle, null)?.Dispose();
            }

            if (cleanupFailed)
            {
                throw new IOException(
                    "The staged replay could not be removed safely.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private static bool DeleteIfIdentityMatches(
        string path,
        ExecutableFileIdentity expectedIdentity)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        SafeFileHandle handle = DeleteNativeMethods.CreateFileW(
            path,
            DeleteNativeMethods.Delete | DeleteNativeMethods.FileReadAttributes,
            DeleteNativeMethods.FileShareRead |
            DeleteNativeMethods.FileShareWrite |
            DeleteNativeMethods.FileShareDelete,
            lpSecurityAttributes: nint.Zero,
            DeleteNativeMethods.OpenExisting,
            DeleteNativeMethods.FileFlagOpenReparsePoint,
            hTemplateFile: nint.Zero);
        if (handle.IsInvalid ||
            !NativeMethods.GetFileInformationByHandle(
                handle,
                out NativeFileInformation information))
        {
            handle.Dispose();
            return !File.Exists(path);
        }

        if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
            information.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber ||
            information.FileIndex != expectedIdentity.FileIndex)
        {
            handle.Dispose();
            return false;
        }

        var disposition = new FileDispositionInformationEx
        {
            Flags =
                FileDispositionDelete |
                FileDispositionPosixSemantics |
                FileDispositionIgnoreReadonlyAttribute,
        };
        bool deleted = DeleteNativeMethods.SetFileInformationByHandle(
            handle.DangerousGetHandle(),
            FileInformationClass.FileDispositionInfoEx,
            ref disposition,
            checked((uint)Marshal.SizeOf<FileDispositionInformationEx>()));
        handle.Dispose();
        return deleted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        public uint Flags;
    }

    private enum FileInformationClass
    {
        FileDispositionInfoEx = 21,
    }

    private const uint FileDispositionDelete = 0x0000_0001;
    private const uint FileDispositionPosixSemantics = 0x0000_0002;
    private const uint FileDispositionIgnoreReadonlyAttribute = 0x0000_0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    private static class DeleteNativeMethods
    {
        internal const uint Delete = 0x0001_0000;
        internal const uint FileListDirectory = 0x0000_0001;
        internal const uint ReadControl = 0x0002_0000;
        internal const uint GenericRead = 0x8000_0000;
        internal const uint GenericWrite = 0x4000_0000;
        internal const uint FileReadAttributes = 0x0000_0080;
        internal const uint FileShareRead = 0x0000_0001;
        internal const uint FileShareWrite = 0x0000_0002;
        internal const uint FileShareDelete = 0x0000_0004;
        internal const uint OpenExisting = 3;
        internal const uint CreateNew = 1;
        internal const uint FileFlagOpenReparsePoint = 0x0020_0000;
        internal const uint FileFlagBackupSemantics = 0x0200_0000;
        internal const uint FileFlagOverlapped = 0x4000_0000;
        internal const uint FileFlagSequentialScan = 0x0800_0000;
        internal const uint FileFlagWriteThrough = 0x8000_0000;
        internal const int ErrorFileExists = 80;
        internal const int ErrorAlreadyExists = 183;

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            nint lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            nint hFile,
            FileInformationClass fileInformationClass,
            ref FileDispositionInformationEx lpFileInformation,
            uint dwBufferSize);
    }

    private static class SecurityNativeMethods
    {
        internal const int ErrorInsufficientBuffer = 122;
        internal const uint OwnerSecurityInformation = 0x0000_0001;
        internal const uint DaclSecurityInformation = 0x0000_0004;

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKernelObjectSecurity(
            SafeFileHandle handle,
            uint securityInformation,
            byte[]? securityDescriptor,
            uint length,
            out uint lengthNeeded);
    }
}
