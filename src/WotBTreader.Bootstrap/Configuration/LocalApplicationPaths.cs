using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.Bootstrap.Configuration;

public sealed record LocalApplicationPaths(
    string Root,
    string ContentStore,
    string Database,
    string Logs,
    string Diagnostics,
    string Rendezvous)
{
    public static LocalApplicationPaths Create(string? rootOverride = null)
    {
        string root = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WotBTreader")
            : Path.GetFullPath(rootOverride);

        // Rendezvous is ephemeral coordination data that must always live in a
        // per-user location. Putting it under a custom data root (which may be
        // shared, removable, or admin-owned) creates ACL hazards. Hardcoding to
        // %LocalAppData% avoids the entire class of permission bricking bugs.
        string rendezvous = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WotBTreader",
            "rendezvous");

        return new LocalApplicationPaths(
            root,
            Path.Combine(root, "content"),
            Path.Combine(root, "treader.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "diagnostics"),
            rendezvous);
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ContentStore);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Diagnostics);
        EnsureRendezvousDirectory();
    }

    /// <summary>
    /// Creates or re-secures the rendezvous directory so only the current user
    /// may read it. Callers that may run after the directory was removed must
    /// use this instead of <see cref="Directory.CreateDirectory(string)"/>, which
    /// would silently restore inherited permissions.
    /// </summary>
    /// <remarks>
    /// The capability must not be published when the directory cannot be
    /// positively verified as owner-only. Permission failures therefore
    /// propagate to the publisher instead of falling back to inherited access.
    /// </remarks>
    public void EnsureRendezvousDirectory() => EnsureOwnerOnlyDirectory(Rendezvous);

    /// <summary>
    /// Applies and verifies an explicit owner-only ACL/mode to a rendezvous
    /// file before a capability is written into it. On Windows the descriptor
    /// is set with protected inheritance and re-read positively; reparse
    /// points are rejected. The caller must have already secured the parent
    /// directory with <see cref="EnsureRendezvousDirectory"/>.
    /// </summary>
    public static void ProtectRendezvousFile(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsOwnerOnlyFile(filePath);
            return;
        }

        ProtectUnixOwnerOnlyFile(filePath);
    }

    /// <summary>
    /// Re-verifies a final rendezvous file (for example after the temporary
    /// file is moved into place) so the published capability record is known
    /// to be a real, owner-only file. On Windows the file object is pinned by
    /// handle (opened without following reparse points) and its DACL is read
    /// from that handle, so a same-user pathname swap cannot redirect the
    /// verification.
    /// </summary>
    public static void VerifyRendezvousFile(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsOwnerOnlyFile(filePath);
            return;
        }

        VerifyUnixOwnerOnlyFile(filePath);
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsOwnerOnlyDirectory(path);
            return;
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, ownerOnly);
        File.SetUnixFileMode(path, ownerOnly);

        UnixFileMode actual = File.GetUnixFileMode(path);
        if (actual != ownerOnly)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory could not be verified as owner-only.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsOwnerOnlyDirectory(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The rendezvous directory must not be a reparse point.");
            }

            directory.SetAccessControl(security);
        }
        else
        {
            directory.Create(security);
        }

        VerifyWindowsOwnerOnlyDirectory(directory, owner);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsOwnerOnlyFile(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The rendezvous file does not exist.",
                path);
        }

        file.Refresh();
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file must not be a reparse point.");
        }

        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        file.SetAccessControl(security);

        VerifyWindowsOwnerOnlyFile(path);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyFile(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows account has no security identifier.");

        // Pin the file object by handle and open without following reparse
        // points, so a same-user swap of the pathname cannot redirect the
        // verification to a different file between the reparse check and the
        // DACL read.
        SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GenericRead,
            NativeMethods.FileShareRead |
            NativeMethods.FileShareWrite |
            NativeMethods.FileShareDelete,
            lpSecurityAttributes: nint.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenReparsePoint,
            hTemplateFile: nint.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is NativeMethods.ErrorFileNotFound
                    or NativeMethods.ErrorPathNotFound)
                {
                    throw new FileNotFoundException(
                        "The rendezvous file does not exist.",
                        path);
                }

                throw new IOException(
                    $"The rendezvous file could not be opened (Win32 error {error}).");
            }

            if (!NativeMethods.GetFileInformationByHandle(
                    handle,
                    out NativeFileInformation information) ||
                (information.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "The rendezvous file must not be a reparse point.");
            }

            VerifyWindowsOwnerOnlyFileSecurity(handle, owner);
        }
        finally
        {
            handle.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyFileSecurity(
        SafeFileHandle handle,
        SecurityIdentifier expectedOwner)
    {
        uint requestedInformation =
            NativeMethods.OwnerSecurityInformation |
            NativeMethods.DaclSecurityInformation;
        if (!NativeMethods.GetKernelObjectSecurity(
                handle,
                requestedInformation,
                securityDescriptor: null,
                length: 0,
                out uint required) &&
            Marshal.GetLastPInvokeError() !=
                NativeMethods.ErrorInsufficientBuffer)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file security is unavailable.");
        }

        if (required == 0 || required > 64 * 1024)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file security is invalid.");
        }

        byte[] bytes = new byte[required];
        if (!NativeMethods.GetKernelObjectSecurity(
                handle,
                requestedInformation,
                bytes,
                checked((uint)bytes.Length),
                out _))
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file security is unavailable.");
        }

        var descriptor = new RawSecurityDescriptor(bytes, offset: 0);
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
            ace.InheritanceFlags != InheritanceFlags.None ||
            ace.PropagationFlags != PropagationFlags.None)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file security is not owner-only.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileInformation
    {
        private readonly uint _fileAttributes;
        private readonly FILETIME _creationTime;
        private readonly FILETIME _lastAccessTime;
        private readonly FILETIME _lastWriteTime;
        private readonly uint _volumeSerialNumber;
        private readonly uint _fileSizeHigh;
        private readonly uint _fileSizeLow;
        private readonly uint _numberOfLinks;
        private readonly uint _fileIndexHigh;
        private readonly uint _fileIndexLow;

        public FileAttributes FileAttributes => (FileAttributes)_fileAttributes;
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x8000_0000;
        internal const uint FileShareRead = 0x0000_0001;
        internal const uint FileShareWrite = 0x0000_0002;
        internal const uint FileShareDelete = 0x0000_0004;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOpenReparsePoint = 0x0020_0000;
        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const uint OwnerSecurityInformation = 0x0000_0001;
        internal const uint DaclSecurityInformation = 0x0000_0004;
        internal const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out NativeFileInformation lpFileInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKernelObjectSecurity(
            SafeFileHandle handle,
            uint securityInformation,
            byte[]? securityDescriptor,
            uint length,
            out uint lengthNeeded);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ProtectUnixOwnerOnlyFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The rendezvous file does not exist.",
                path);
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, ownerOnly);
        VerifyUnixOwnerOnlyFile(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyUnixOwnerOnlyFile(string path)
    {
        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite;
        UnixFileMode actual = File.GetUnixFileMode(path);
        if (actual != ownerOnly)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous file could not be verified as owner-only.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsOwnerOnlyDirectory(
        DirectoryInfo directory,
        SecurityIdentifier expectedOwner)
    {
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory identity could not be verified.");
        }

        DirectorySecurity actual = directory.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!actual.AreAccessRulesProtected ||
            !expectedOwner.Equals(actual.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory owner or inheritance boundary is unsafe.");
        }

        AuthorizationRuleCollection rules = actual.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        if (rules.Count != 1 ||
            rules[0] is not FileSystemAccessRule rule ||
            rule.IsInherited ||
            rule.AccessControlType != AccessControlType.Allow ||
            !expectedOwner.Equals(rule.IdentityReference) ||
            (rule.FileSystemRights & FileSystemRights.FullControl) !=
                FileSystemRights.FullControl ||
            rule.InheritanceFlags !=
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
        {
            throw new UnauthorizedAccessException(
                "The rendezvous directory access rules are not owner-only.");
        }
    }
}

public sealed record TreaderBootstrapOptions(
    string? ApplicationDataRoot = null,
    string? GameRoot = null,
    string? GameUserDataRoot = null,
    TimeSpan? OfflineReplayEvidenceLifetime = null,
    TimeSpan? LifecycleEvidenceTimeout = null,
    string? InstructionSnapshotHelperPath = null,
    string? InstructionSnapshotHelperSha256 = null,
    bool? SqliteConnectionPooling = null);
