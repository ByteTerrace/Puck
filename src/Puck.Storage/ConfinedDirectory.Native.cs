using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Storage;

internal sealed partial class ConfinedDirectory {
    private static SafeFileHandle? OpenWindows(string path, bool directory, bool create, bool write, bool exclusive = false, bool newFile = false) {
        // OPEN_REPARSE_POINT opens the link itself for inspection. No truncation occurs before inspection.
        var handle = CreateFile(
            WindowsPath(path: path),
            (directory
            ? 0x80u
            : (write
                ? 0xC0000000u
                : 0x80000000u)),
            (exclusive
            ? 0u
            : 1u),
            0,
            (newFile
            ? 1u
            : (create
                ? 4u
                : 3u)),
            0x02200000,
            0
        );

        if (handle.IsInvalid) {
            var code = Marshal.GetLastPInvokeError();

            handle.Dispose();
            if (
                (code is 2 or 3) &&
                !create
            ) { return null; }
            throw new IOException(
                "Cannot open confined storage entry",
                new System.ComponentModel.Win32Exception(error: code)
            );
        }
        try {
            if (!GetFileInformationByHandle(
                handle: handle,
                info: out var info
            )) { throw Error(message: "Cannot inspect storage handle"); }
            if (
                ((info.Attributes & 0x400) != 0) ||
                (((info.Attributes & 0x10) != 0) != directory) ||
                (!directory && (info.Links != 1))
            ) {
                throw new IOException(message: "Storage refuses reparse points, hard links, and unexpected entry types.");
            }
            return handle;
        } catch { handle.Dispose(); throw; }
    }
    // The directory walk has already resolved and confined a local absolute path. Use extended
    // syntax only at the native boundary so blob and temporary names are not limited by MAX_PATH.
    private static string WindowsPath(string path) => (@"\\?\" + path.Replace(
        newChar: '\\',
        oldChar: '/'
    ));
    private static SafeFileHandle? OpenLinux(int parent, string name, bool directory, bool create, bool write, bool exclusive = false, bool newFile = false) {
        const int NoFollow = 0x20000, CloseOnExec = 0x80000, NonBlock = 0x800, DirectoryFlag = 0x10000;
        var flags = NoFollow | CloseOnExec | NonBlock | (directory
            ? DirectoryFlag
            : (write
                ? 2
                : 0)) | ((create && !directory)
            ? 0x40
            : 0) | (newFile
            ? 0x80
            : 0
        );
        var fd = OpenAt(
            flags: flags,
            mode: 0x180,
            name: name,
            parent: parent
        ); // 0600 for files; mkdirat below uses 0700.

        if (
            (fd < 0) &&
            directory &&
            create &&
            (Marshal.GetLastPInvokeError() == 2)
        ) {
            if (
                (MkdirAt(
                mode: 0x1C0,
                name: name,
                parent: parent
            ) != 0) &&
                (Marshal.GetLastPInvokeError() != 17)
            ) { throw Error(message: "Cannot create storage directory"); }
            fd = OpenAt(
                flags: flags,
                mode: 0,
                name: name,
                parent: parent
            );
        }
        if (fd < 0) {
            if (
                !create &&
                (Marshal.GetLastPInvokeError() == 2)
            ) { return null; }
            throw Error(message: "Cannot open confined storage entry");
        }
        var handle = new SafeFileHandle(
            fd,
            ownsHandle: true
        );

        try {
            if (Fstat(
                fd: fd,
                stat: out var stat
            ) != 0) { throw Error(message: "Cannot inspect storage handle"); }
            if (
                ((stat.Mode & 0xF000) != (directory
                ? 0x4000
                : 0x8000)) ||
                (!directory && (stat.Links != 1))
            ) {
                throw new IOException(message: "Storage refuses symbolic links, hard links, and nonregular files.");
            }
            if (
                exclusive &&
                (Flock(
                fd: fd,
                operation: 6
            ) != 0)
            ) { throw Error(message: "Storage lock is busy"); } // LOCK_EX | LOCK_NB
            return handle;
        } catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    // Linux x86-64 struct stat. Other architectures fail closed before reaching this ABI.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxStat {
        [FieldOffset(16)] public ulong Links;
        [FieldOffset(24)] public uint Mode;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation info);
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string target, uint flags);
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(int parent, string name, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MkdirAt(int parent, string name, uint mode);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int Fstat(int fd, out LinuxStat stat);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int fd, int operation);
    [LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt(int oldParent, string oldName, int newParent, string newName);
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(int parent, string name, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);
}
