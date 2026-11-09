using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Confinement;

// Linux x64: every lookup below a held descriptor is openat/fstatat/unlinkat/renameat relative to it with O_NOFOLLOW or
// AT_SYMLINK_NOFOLLOW, so a symbolic link is never followed. Holding a directory is an flock on its descriptor:
// LOCK_SH for a process that runs from it, LOCK_EX|LOCK_NB for a removal, which fails while any shared holder lives.
// The kernel releases a holder's locks when its last descriptor closes, a crash included. Sources: the openat(2),
// fstatat(2), flock(2), rename(2) (renameat2, RENAME_NOREPLACE), unlink(2) and proc_pid_maps(5) manual pages.
public sealed partial class ConfinedDirectory {
    private static partial class Linux {
        public const int ReadOnly = 0, WriteOnly = 1, ReadWrite = 2, Create = 0x40, Exclusive = 0x80, NonBlock = 0x800, DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
        public const int SymlinkNoFollow = 0x100, RemoveDirectory = 0x200;
        public const int LockShared = 1, LockExclusive = 2, LockNonBlocking = 4;
        public const int NoEntry = 2, WouldBlock = 11, AccessDenied = 13, Exists = 17, NotDirectory = 20, InvalidArgument = 22, NotImplemented = 38, Loop = 40;

        private const uint ModeMask = 0xF000, ModeDirectory = 0x4000, ModeRegular = 0x8000, ModeLink = 0xA000;

        // Linux x86-64 struct stat. Other architectures fail closed before reaching this ABI.
        [StructLayout(LayoutKind.Explicit, Size = 144)]
        public struct Stat {
            [FieldOffset(0)] public ulong Device;
            [FieldOffset(8)] public ulong Inode;
            [FieldOffset(16)] public ulong Links;
            [FieldOffset(24)] public uint Mode;
        }

        public static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
        public static string ProcPath(SafeFileHandle handle) => $"/proc/self/fd/{Fd(handle: handle)}";
        public static SafeFileHandle OpenPath(string path) {
            var fd = OpenAt(flags: ReadOnly | DirectoryFlag | CloseOnExec, mode: 0, name: path, parent: -100);

            if (fd < 0) {
                var code = Marshal.GetLastPInvokeError();

                throw ((code == NoEntry)
                    ? new DirectoryNotFoundException(message: $"'{path}' does not exist.", innerException: new Win32Exception(error: code))
                    : new IOException(message: $"'{path}' cannot be opened.", innerException: new Win32Exception(error: code)));
            }
            return new SafeFileHandle(ownsHandle: true, preexistingHandle: fd);
        }
        // Opens or creates name below parent without following a link; null when it is missing and not created.
        public static SafeFileHandle? Open(SafeFileHandle parent, string name, bool directory, bool create, bool write, bool newFile) {
            var flags = NoFollow | CloseOnExec | NonBlock | (directory ? DirectoryFlag : (write ? ReadWrite : ReadOnly)) |
                ((create && !directory) ? Create : 0) | (newFile ? Exclusive : 0);
            var fd = OpenAt(flags: flags, mode: 0x180, name: name, parent: Fd(handle: parent));

            if ((fd < 0) && directory && create && (Marshal.GetLastPInvokeError() == NoEntry)) {
                if ((MkdirAt(mode: 0x1C0, name: name, parent: Fd(handle: parent)) != 0) && (Marshal.GetLastPInvokeError() != Exists)) {
                    throw Error(message: $"Cannot create confined directory '{name}'");
                }
                fd = OpenAt(flags: flags, mode: 0, name: name, parent: Fd(handle: parent));
            }
            if (fd < 0) {
                var code = Marshal.GetLastPInvokeError();

                if (!create && (code == NoEntry)) { return null; }
                // O_NOFOLLOW refuses a link with ELOOP, or with ENOTDIR when O_DIRECTORY is checked first.
                var link = ((code is Loop or NotDirectory) && (StatAt(name: name, parent: parent) is { } stat) && (Kind(stat: stat) == ConfinedEntryKind.Link));

                throw Error(code: code, message: (link
                    ? $"'{name}' is a link; confined access refuses it."
                    : ((code == NotDirectory) ? $"'{name}' is not a directory; confined access refuses it." : $"Cannot open confined entry '{name}'")));
            }
            var handle = new SafeFileHandle(ownsHandle: true, preexistingHandle: fd);

            try {
                var stat = StatOf(handle: handle);

                if (((stat.Mode & ModeMask) != (directory ? ModeDirectory : ModeRegular)) || (!directory && (stat.Links != 1))) {
                    throw new IOException(message: $"'{name}' is a link, a hard-linked file or an unexpected entry type; confined access refuses it.");
                }
                return handle;
            } catch { handle.Dispose(); throw; }
        }
        public static Stat StatOf(SafeFileHandle handle) => ((Fstat(fd: Fd(handle: handle), stat: out var stat) == 0)
            ? stat
            : throw Error(message: "Cannot inspect confined handle"));
        // The entry itself, never what a link names; null when it is missing.
        public static Stat? StatAt(SafeFileHandle parent, string name) {
            if (FstatAt(flags: SymlinkNoFollow, name: name, parent: Fd(handle: parent), stat: out var stat) == 0) { return stat; }
            return ((Marshal.GetLastPInvokeError() == NoEntry) ? null : throw Error(message: $"Cannot inspect confined entry '{name}'"));
        }
        public static ConfinedEntryKind Kind(Stat stat) => ((stat.Mode & ModeMask) switch {
            ModeDirectory => ConfinedEntryKind.Directory,
            ModeRegular => ConfinedEntryKind.File,
            ModeLink => ConfinedEntryKind.Link,
            _ => ConfinedEntryKind.Other,
        });
        public static ConfinedIdentity Identity(Stat stat) => new(File: stat.Inode, Volume: stat.Device);
        public static List<ConfinedEntry> Entries(SafeFileHandle handle) {
            var entries = new List<ConfinedEntry>();

            // The descriptor's /proc link reaches the held directory object, wherever it has been moved.
            foreach (var path in Directory.EnumerateFileSystemEntries(path: ProcPath(handle: handle))) {
                var name = System.IO.Path.GetFileName(path: path);

                if (StatAt(name: name, parent: handle) is { } stat) {
                    entries.Add(item: new ConfinedEntry(Kind: Kind(stat: stat), Name: name));
                }
            }
            return entries;
        }
        // True when the lock was taken; false when another holder's lock refuses it.
        public static bool TryLock(SafeFileHandle handle, int operation) {
            if (Flock(fd: Fd(handle: handle), operation: operation | LockNonBlocking) == 0) { return true; }
            return ((Marshal.GetLastPInvokeError() == WouldBlock) ? false : throw Error(message: "Cannot lock confined entry"));
        }
        public static void Rename(SafeFileHandle sourceParent, string sourceName, SafeFileHandle destinationParent, string destinationName) {
            if (RenameAt2(flags: 1, newName: destinationName, newParent: Fd(handle: destinationParent), oldName: sourceName, oldParent: Fd(handle: sourceParent)) == 0) { return; }
            var code = Marshal.GetLastPInvokeError();

            // A file system without RENAME_NOREPLACE: the destination is a fresh name the caller chose.
            if ((code is InvalidArgument or NotImplemented) &&
                (RenameAt(newName: destinationName, newParent: Fd(handle: destinationParent), oldName: sourceName, oldParent: Fd(handle: sourceParent)) == 0)) { return; }
            throw Error(message: $"Cannot move confined entry '{sourceName}' to '{destinationName}'");
        }
        public static void RenameReplace(SafeFileHandle parent, string sourceName, string destinationName) {
            if (RenameAt(newName: destinationName, newParent: Fd(handle: parent), oldName: sourceName, oldParent: Fd(handle: parent)) != 0) {
                throw Error(message: $"Cannot replace confined file '{destinationName}'");
            }
        }
        // fsync(2) on a directory makes its entries (a rename into it, a file created in it) durable.
        public static void Flush(SafeFileHandle directory) {
            if (Fsync(fd: Fd(handle: directory)) != 0) { throw Error(message: "Cannot flush confined directory"); }
        }
        // False when the entry was already gone.
        public static bool Unlink(SafeFileHandle parent, string name, bool directory) {
            if (UnlinkAt(flags: (directory ? RemoveDirectory : 0), name: name, parent: Fd(handle: parent)) == 0) { return true; }
            return ((Marshal.GetLastPInvokeError() == NoEntry) ? false : throw Error(message: $"Cannot remove confined entry '{name}'"));
        }
        public static SafeFileHandle? OpenForRemoval(SafeFileHandle parent, string name, bool directory, bool writeOnly, out int error) {
            var fd = OpenAt(flags: (writeOnly ? WriteOnly : ReadOnly) | (directory ? DirectoryFlag : 0) | NoFollow | CloseOnExec | NonBlock, mode: 0, name: name, parent: Fd(handle: parent));

            error = ((fd < 0) ? Marshal.GetLastPInvokeError() : 0);
            return ((fd < 0) ? null : new SafeFileHandle(ownsHandle: true, preexistingHandle: fd));
        }
        public static bool IsBusy(IOException exception) => ((exception.InnerException is Win32Exception native) && (native.NativeErrorCode == WouldBlock));
        // Whether a mapping of this process names the file held by parent/name: same inode and the same current path,
        // both as the kernel reports them. The kernel prints a mapping's device in a form that differs from stat's
        // st_dev on some file systems (btrfs subvolumes), so the path takes the device's place.
        public static bool IsMapped(SafeFileHandle parent, string name) {
            if ((StatAt(name: name, parent: parent) is not { } stat) || (Kind(stat: stat) != ConfinedEntryKind.File)) { return false; }
            var directory = new FileInfo(fileName: ProcPath(handle: parent)).LinkTarget;
            var expected = ((directory is null) ? null : System.IO.Path.Join(path1: directory, path2: name));

            foreach (var line in File.ReadLines(path: "/proc/self/maps")) {
                var fields = line.Split(count: 6, options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

                if ((fields.Length == 6) && ulong.TryParse(s: fields[4], result: out var inode) && (inode == stat.Inode) &&
                    string.Equals(a: fields[5].Trim(), b: expected, comparisonType: StringComparison.Ordinal)) {
                    return true;
                }
            }
            return false;
        }
        public static IOException Error(string message) => Error(code: Marshal.GetLastPInvokeError(), message: message);
        public static IOException Error(string message, int code) => new(message: message, innerException: new Win32Exception(error: code));

        [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int OpenAt(int parent, string name, int flags, uint mode);
        [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int MkdirAt(int parent, string name, uint mode);
        [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static partial int Fstat(int fd, out Stat stat);
        [LibraryImport("libc", EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int FstatAt(int parent, string name, out Stat stat, int flags);
        [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static partial int Flock(int fd, int operation);
        [LibraryImport("libc", EntryPoint = "renameat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int RenameAt(int oldParent, string oldName, int newParent, string newName);
        [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int RenameAt2(int oldParent, string oldName, int newParent, string newName, uint flags);
        [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int UnlinkAt(int parent, string name, int flags);
        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static partial int Fsync(int fd);
    }
}
