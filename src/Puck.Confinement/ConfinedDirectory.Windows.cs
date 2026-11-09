using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Confinement;

// Windows: every lookup below a held handle goes through NtCreateFile with that handle as its RootDirectory and
// FILE_OPEN_REPARSE_POINT, so a link is opened as itself and refused by its attributes, never followed. A directory is
// held with read-type access and without FILE_SHARE_DELETE, so while it is held no other open may rename or delete it;
// an open that asks for DELETE (a removal) fails with a sharing violation while anyone else holds it. Renames and
// deletions act on the handle of the object itself (FileRenameInformation, FileDispositionInformationEx), never on a
// path. Microsoft documents NtCreateFile, OBJECT_ATTRIBUTES.RootDirectory, FILE_RENAME_INFORMATION and
// FILE_DISPOSITION_INFORMATION_EX in the Windows Driver Kit reference; kernel32's SetFileInformationByHandle refuses a
// rename with a RootDirectory (ERROR_INVALID_PARAMETER), which is why the rename calls ntdll directly.
public sealed partial class ConfinedDirectory {
    private static partial class Windows {
        public const uint Delete = 0x10000, ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, Synchronize = 0x100000, Traverse = 0x20;
        public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        public const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
        public const uint DirectoryAccess = ListDirectory | Traverse | ReadAttributes | Synchronize;

        private const uint MaximumPath = 32768;
        private const uint MoveFileReplaceExisting = 0x1, MoveFileWriteThrough = 0x8;
        private const uint FileOpen = 1, FileCreate = 2, FileOpenIf = 3;
        private const uint DirectoryFile = 0x1, NonDirectoryFile = 0x40, SynchronousIo = 0x20, OpenReparsePoint = 0x200000;
        private const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;
        private const int NotFound = unchecked((int)0xC0000034), PathNotFound = unchecked((int)0xC000003A), NoSuchFile = unchecked((int)0xC000000F),
            SharingViolation = unchecked((int)0xC0000043), DeletePending = unchecked((int)0xC0000056), DirectoryNotEmpty = unchecked((int)0xC0000101);

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes { public int Length; public nint RootDirectory, ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock { public nint Status, Information; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        public static string NativePath(string path) => (path.StartsWith(comparisonType: StringComparison.Ordinal, value: @"\\?\")
            ? path
            : (@"\\?\" + path.Replace(newChar: '\\', oldChar: '/')));
        // A volume root, or an anchor whose own path the host chose: the OS resolves it as given.
        public static SafeFileHandle OpenPath(string path) {
            var handle = CreateFile(NativePath(path: path), DirectoryAccess, ShareRead | ShareWrite, 0, 3, 0x02000000, 0);

            if (handle.IsInvalid) {
                var code = Marshal.GetLastPInvokeError();

                handle.Dispose();
                throw ((code is 2 or 3)
                    ? new DirectoryNotFoundException(message: $"'{path}' does not exist.", innerException: new Win32Exception(error: code))
                    : new IOException(message: $"'{path}' cannot be opened.", innerException: new Win32Exception(error: code)));
            }
            return handle;
        }
        // Opens or creates name below parent without following a link. Returns null when it is missing (and not created)
        // or, with busyIsNull, when another holder's sharing mode refuses the open.
        public static unsafe SafeFileHandle? Open(SafeFileHandle parent, string name, uint access, uint share, bool directory, bool create, bool newFile, bool busyIsNull) {
            var disposition = (newFile ? FileCreate : (create ? FileOpenIf : FileOpen));
            var options = SynchronousIo | OpenReparsePoint | (directory ? DirectoryFile : NonDirectoryFile);
            int status;
            nint raw;

            fixed (char* text = name) {
                var unicode = new UnicodeString { Buffer = ((nint)text), Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)) };
                var attributes = new ObjectAttributes { Attributes = 0x40, Length = sizeof(ObjectAttributes), ObjectName = ((nint)(&unicode)), RootDirectory = parent.DangerousGetHandle() };
                IoStatusBlock io;

                status = NtCreateFile(access: access | ReadAttributes | Synchronize, allocationSize: null, attributes: &attributes, disposition: disposition, extendedAttributes: null, extendedAttributesLength: 0, fileAttributes: 0x80, handle: &raw, options: options, share: share, status: &io);
            }
            if (status == 0) {
                var handle = new SafeFileHandle(ownsHandle: true, preexistingHandle: raw);

                try {
                    var info = Information(handle: handle);

                    if (((info.Attributes & AttributeReparsePoint) != 0) || (((info.Attributes & AttributeDirectory) != 0) != directory) || (!directory && (info.Links != 1))) {
                        throw new IOException(message: $"'{name}' is a link, a hard-linked file or an unexpected entry type; confined access refuses it.");
                    }
                    return handle;
                } catch { handle.Dispose(); throw; }
            }
            if ((status is NotFound or PathNotFound or NoSuchFile) && !create) { return null; }
            if ((status == SharingViolation) && busyIsNull) { return null; }
            throw Failure(message: $"Cannot open confined entry '{name}'", status: status);
        }
        // Opens whatever name is (file, directory or link) for inspection and removal; never follows it.
        public static unsafe SafeFileHandle? OpenAny(SafeFileHandle parent, string name, uint access, uint share, out bool isDirectory, out bool isLink) {
            int status;
            nint raw;

            fixed (char* text = name) {
                var unicode = new UnicodeString { Buffer = ((nint)text), Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)) };
                var attributes = new ObjectAttributes { Attributes = 0x40, Length = sizeof(ObjectAttributes), ObjectName = ((nint)(&unicode)), RootDirectory = parent.DangerousGetHandle() };
                IoStatusBlock io;

                status = NtCreateFile(access: access | Synchronize, allocationSize: null, attributes: &attributes, disposition: FileOpen, extendedAttributes: null, extendedAttributesLength: 0, fileAttributes: 0x80, handle: &raw, options: SynchronousIo | OpenReparsePoint, share: share, status: &io);
            }
            if (status is NotFound or PathNotFound or NoSuchFile or DeletePending) { isDirectory = isLink = false; return null; }
            if (status != 0) { throw Failure(message: $"Cannot open confined entry '{name}'", status: status); }
            var handle = new SafeFileHandle(ownsHandle: true, preexistingHandle: raw);

            try {
                var info = Information(handle: handle);

                isDirectory = ((info.Attributes & AttributeDirectory) != 0);
                isLink = ((info.Attributes & AttributeReparsePoint) != 0);
                return handle;
            } catch { handle.Dispose(); throw; }
        }
        public static unsafe ConfinedIdentity Identity(SafeFileHandle handle) {
            var buffer = stackalloc byte[24];

            if (!GetFileInformationByHandleEx(handle: handle, information: buffer, informationClass: 18, size: 24)) { throw Error(message: "Cannot identify confined handle"); }
            return new ConfinedIdentity(File: new UInt128(lower: *((ulong*)(buffer + 8)), upper: *((ulong*)(buffer + 16))), Volume: *((ulong*)buffer));
        }
        public static unsafe List<ConfinedEntry> Entries(SafeFileHandle handle) {
            const int Size = (64 * 1024);
            var entries = new List<ConfinedEntry>();
            var buffer = new byte[Size];
            var restart = true;

            fixed (byte* start = buffer) {
                while (GetFileInformationByHandleEx(handle: handle, information: start, informationClass: (restart ? 15 : 14), size: Size)) {
                    var offset = 0;

                    restart = false;
                    while (true) {
                        var entry = (start + offset);
                        var attributes = *((uint*)(entry + 56));
                        var name = new string(length: checked((int)(*((uint*)(entry + 60)) / 2)), startIndex: 0, value: ((char*)(entry + 68)));

                        if (name is not ("." or "..")) {
                            entries.Add(item: new ConfinedEntry(Kind: (((attributes & AttributeReparsePoint) != 0)
                                ? ConfinedEntryKind.Link
                                : (((attributes & AttributeDirectory) != 0) ? ConfinedEntryKind.Directory : ConfinedEntryKind.File)), Name: name));
                        }
                        var next = *((uint*)entry);

                        if (next == 0) { break; }
                        offset += checked((int)next);
                    }
                }
            }
            var code = Marshal.GetLastPInvokeError();

            return ((code == 18) ? entries : throw Error(message: "Cannot list confined directory"));
        }
        // Replaces destination with source, both full paths, and returns only once the move is on the disk: MoveFileExW
        // with MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH, the one Windows call whose documentation promises
        // that ("The function does not return until the file is actually moved on the disk").
        public static void MoveDurably(string source, string destination) {
            if (!MoveFileEx(existingFileName: NativePath(path: source), flags: (MoveFileReplaceExisting | MoveFileWriteThrough), newFileName: NativePath(path: destination))) {
                var code = Marshal.GetLastPInvokeError();

                throw new IOException(message: $"Cannot replace '{destination}' durably.", innerException: new Win32Exception(error: code));
            }
        }
        // FileRenameInformation with RootDirectory: the held object moves under destination, whatever its path is now. It
        // never replaces an existing entry.
        public static unsafe void Rename(SafeFileHandle handle, SafeFileHandle destination, string name) {
            var size = (24 + (name.Length * 2));
            var buffer = stackalloc byte[size];

            new Span<byte>(length: size, pointer: buffer).Clear();
            *((nint*)(buffer + 8)) = destination.DangerousGetHandle();
            *((uint*)(buffer + 16)) = checked((uint)(name.Length * 2));
            name.AsSpan().CopyTo(destination: new Span<char>(pointer: (buffer + 20), length: name.Length));
            IoStatusBlock io;
            var status = NtSetInformationFile(handle: handle, information: buffer, informationClass: 10, length: checked((uint)size), status: &io);

            if (status != 0) { throw Failure(message: $"Cannot move confined entry to '{name}'", status: status); }
        }
        // POSIX semantics remove the name at once, so a directory whose children were just removed is empty.
        public static unsafe void Remove(SafeFileHandle handle) {
            var flags = 0x1u | 0x2u | 0x10u;

            if (SetFileInformationByHandle(handle: handle, information: &flags, informationClass: 21, size: 4)) { return; }
            var code = Marshal.GetLastPInvokeError();

            // ReFS can return access denied for a read-only file even with IGNORE_READONLY_ATTRIBUTE.
            // Change only the held object, never a path (which could now name a link or a replacement).
            if ((code == 5) && ((Information(handle: handle).Attributes & ((uint)FileAttributes.ReadOnly)) != 0)) {
                var info = Information(handle: handle);


                if (((info.Attributes & AttributeDirectory) == 0) && (info.Links != 1)) {
                    throw new IOException(message: "A read-only hard-linked file cannot have its attributes changed by confined removal.");
                }
                var attributes = ((FileAttributes)info.Attributes) & ~FileAttributes.ReadOnly;

                File.SetAttributes(fileHandle: handle, fileAttributes: ((attributes == 0) ? FileAttributes.Normal : attributes));
                if (SetFileInformationByHandle(handle: handle, information: &flags, informationClass: 21, size: 4)) { return; }
                code = Marshal.GetLastPInvokeError();
            }
            var classic = ((byte)1);

            if ((code != 87) || !SetFileInformationByHandle(handle: handle, information: &classic, informationClass: 4, size: 1)) {
                throw new IOException(message: ((code is 145) ? "Confined directory is not empty." : "Cannot remove confined entry"), innerException: new Win32Exception(error: code));
            }
        }
        // The file now named by the NT path a mapping's file was opened by (K32GetMappedFileNameW reports the open-time
        // name, in its device form, and never a later one), or null when nothing is there.
        public static SafeFileHandle? OpenNtPath(string ntPath) {
            var handle = CreateFile(access: ReadAttributes, disposition: 3, flags: 0x02000000, name: (@"\\?\GLOBALROOT" + ntPath), security: 0, share: ShareRead | ShareWrite | ShareDelete, template: 0);

            if (!handle.IsInvalid) { return handle; }
            handle.Dispose();
            return null;
        }
        public static unsafe string? MappedNtPath(nint address) {
            var buffer = new char[MaximumPath];

            fixed (char* start = buffer) {
                var length = GetMappedFileName(address: address, name: start, process: -1, size: MaximumPath);

                return ((length == 0) ? null : new string(length: ((int)length), startIndex: 0, value: start));
            }
        }
        public static bool IsBusy(IOException exception) => ((exception.InnerException is Win32Exception native) && (native.NativeErrorCode is 32 or 33));

        private static FileInformation Information(SafeFileHandle handle) => (GetFileInformationByHandle(handle: handle, info: out var info)
            ? info
            : throw Error(message: "Cannot inspect confined handle"));
        private static IOException Failure(string message, int status) {
            var code = checked((int)RtlNtStatusToDosError(status: status));

            return ((code is 2 or 3)
                ? new FileNotFoundException(message: $"{message}: it does not exist.", innerException: new Win32Exception(error: code))
                : new IOException(message: message, innerException: new Win32Exception(error: code)));
        }
        private static IOException Error(string message) => new(message: message, innerException: new Win32Exception(error: Marshal.GetLastPInvokeError()));
        [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [LibraryImport("ntdll.dll")]
        private static unsafe partial int NtCreateFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatusBlock* status, long* allocationSize,
            uint fileAttributes, uint share, uint disposition, uint options, void* extendedAttributes, uint extendedAttributesLength);
        [LibraryImport("ntdll.dll")]
        private static unsafe partial int NtSetInformationFile(SafeFileHandle handle, IoStatusBlock* status, void* information, uint length, int informationClass);
        [LibraryImport("ntdll.dll")]
        private static partial uint RtlNtStatusToDosError(int status);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, void* information, uint size);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, void* information, uint size);
        [LibraryImport("kernel32.dll", EntryPoint = "K32GetMappedFileNameW", SetLastError = true)]
        private static unsafe partial uint GetMappedFileName(nint process, nint address, char* name, uint size);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)]
        public static partial nint GetModuleHandle(nint name);
    }
}
