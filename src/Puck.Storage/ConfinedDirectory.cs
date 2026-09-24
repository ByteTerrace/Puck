using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Storage;

// Directory capabilities are pinned until disposal. Windows denies ancestor write/rename/delete while using paths;
// Linux resolves each component relative to a pinned descriptor. Neither follows links during a lookup.
internal sealed partial class ConfinedDirectory : IDisposable {
    private readonly List<SafeFileHandle> m_handles = [];

    private string m_path = "";

    private SafeFileHandle Handle => m_handles[^1];
    private string AccessPath => (OperatingSystem.IsWindows()
        ? m_path
        : $"/proc/self/fd/{Handle.DangerousGetHandle()}"
    );

    internal static ConfinedDirectory? Open(string path, bool create) {
        var directory = new ConfinedDirectory();

        try {
            var full = Path.GetFullPath(path: path);
            var root = Path.GetPathRoot(path: full)!;

            if (OperatingSystem.IsWindows()) {
                if (root.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "\\\\"
                )) { throw new IOException(message: "Confined storage requires a local volume."); }
                directory.m_path = root;
                directory.m_handles.Add(item: OpenWindows(
                    root,
                    directory: true,
                    create: false,
                    write: false
                )!);
            } else if (
                OperatingSystem.IsLinux() &&
                (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            ) {
                directory.m_path = root;
                directory.m_handles.Add(item: OpenLinux(
                    -100,
                    root,
                    directory: true,
                    create: false,
                    write: false
                )!);
            } else { throw new PlatformNotSupportedException(message: "Confined storage supports Windows and Linux x64."); }
            foreach (var segment in full[root.Length..].Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: Path.DirectorySeparatorChar
            )) {
                if (!directory.Descend(
                    create: create,
                    segment: segment
                )) { directory.Dispose(); return null; }
            }
            return directory;
        } catch { directory.Dispose(); throw; }
    }

    private bool Descend(string segment, bool create) {
        var nextPath = Path.Combine(
            path1: m_path,
            path2: segment
        );
        SafeFileHandle? next;

        if (OperatingSystem.IsWindows()) {
            next = OpenWindows(
                nextPath,
                directory: true,
                create: false,
                write: false
            );
            if (
                (next is null) &&
                create
            ) {
                Directory.CreateDirectory(path: nextPath);
                next = OpenWindows(
                    nextPath,
                    directory: true,
                    create: false,
                    write: false
                );
            }
        } else {
            next = OpenLinux(
            Fd(handle: Handle),
            segment,
            directory: true,
            create,
            write: false
        );
        }
        if (next is null) { return false; }
        m_handles.Add(item: next);
        m_path = nextPath;
        return true;
    }

    internal FileStream? OpenFile(string name, bool create = false, bool exclusive = false, bool newFile = false) {
        var handle = (OperatingSystem.IsWindows()
            ? OpenWindows(
                Path.Combine(
                    path1: m_path,
                    path2: name
                ),
                directory: false,
                create,
                write: create,
                exclusive,
                newFile
            )
            : OpenLinux(
                Fd(handle: Handle),
                name,
                directory: false,
                create,
                write: create,
                exclusive,
                newFile
            )
        );

        return ((handle is null)
            ? null
            : new FileStream(
                access: (create
                ? FileAccess.ReadWrite
                : FileAccess.Read),
                handle: handle
            )
        );
    }
    internal FileStream AcquireLock(CancellationToken cancellationToken) {
        var started = Environment.TickCount64;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                return OpenFile(
                ".puck-lock",
                create: true,
                exclusive: true
            )!;
            } catch (IOException exception) when (((exception.InnerException is Win32Exception native) &&
                                                                                       (native.NativeErrorCode is 11 or 32 or 33) && ((Environment.TickCount64 - started) < 5000))) { Thread.Sleep(millisecondsTimeout: 1); }
        }
    }
    internal IEnumerable<string> Entries() => Directory.EnumerateFileSystemEntries(path: AccessPath).Select(selector: Path.GetFileName)!;
    internal bool IsDirectory(string name) {
        // Attribute hints are never used to authorize an open: every subsequent open checks its handle.
        return ((File.GetAttributes(path: Path.Combine(
            path1: AccessPath,
            path2: name
        )) & FileAttributes.Directory) != 0);
    }
    internal ConfinedDirectory? Child(string name) {
        // Keep the parent capability alive in the caller. Linux must use its descriptor, not its old path.
        if (OperatingSystem.IsWindows()) {
            return Open(
            Path.Combine(
                path1: m_path,
                path2: name
            ),
            create: false
        );
        }
        var handle = OpenLinux(
            Fd(handle: Handle),
            name,
            directory: true,
            create: false,
            write: false
        );

        if (handle is null) { return null; }
        var child = new ConfinedDirectory {
            m_path = Path.Combine(
            path1: m_path,
            path2: name
        ),
        };

        child.m_handles.Add(item: handle);
        return child;
    }
    internal void Publish(string temporary, string name) {
        if (OperatingSystem.IsWindows()) {
            if (!MoveFileEx(
                WindowsPath(path: Path.Combine(
                    path1: m_path,
                    path2: temporary
                )),
                WindowsPath(path: Path.Combine(
                    path1: m_path,
                    path2: name
                )),
                9
            )) { throw Error(message: "Cannot publish storage blob"); }
        } else {
            if (
                (RenameAt(
                Fd(handle: Handle),
                temporary,
                Fd(handle: Handle),
                name
            ) != 0) ||
                (Fsync(fd: Fd(handle: Handle)) != 0)
            ) { throw Error(message: "Cannot publish storage blob"); }
        }
    }
    internal void RemoveTemporary(string name) {
        if (OperatingSystem.IsWindows()) {
            File.Delete(path: Path.Combine(
            path1: m_path,
            path2: name
        ));
        } else if (
            (UnlinkAt(
            Fd(handle: Handle),
            name,
            0
        ) != 0) &&
            (Marshal.GetLastPInvokeError() != 2)
        ) { throw Error(message: "Cannot remove storage temporary"); }
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static IOException Error(string message) => new(
        message,
        new Win32Exception(error: Marshal.GetLastPInvokeError())
    );

    public void Dispose() { for (var i = (m_handles.Count - 1); (i >= 0); i--) { m_handles[i].Dispose(); } }
}
