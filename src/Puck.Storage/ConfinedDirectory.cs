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
    private string AccessPath => OperatingSystem.IsWindows() ? m_path : $"/proc/self/fd/{Handle.DangerousGetHandle()}";

    internal static ConfinedDirectory? Open(string path, bool create) {
        var directory = new ConfinedDirectory();
        try {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            if (OperatingSystem.IsWindows()) {
                if (root.StartsWith("\\\\", StringComparison.Ordinal)) { throw new IOException("Confined storage requires a local volume."); }
                directory.m_path = root;
                directory.m_handles.Add(OpenWindows(root, directory: true, create: false, write: false)!);
            } else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) {
                directory.m_path = root;
                directory.m_handles.Add(OpenLinux(-100, root, directory: true, create: false, write: false)!);
            } else { throw new PlatformNotSupportedException("Confined storage supports Windows and Linux x64."); }
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
                if (!directory.Descend(segment, create)) { directory.Dispose(); return null; }
            }
            return directory;
        } catch { directory.Dispose(); throw; }
    }

    private bool Descend(string segment, bool create) {
        var nextPath = Path.Combine(m_path, segment);
        SafeFileHandle? next;
        if (OperatingSystem.IsWindows()) {
            next = OpenWindows(nextPath, directory: true, create: false, write: false);
            if (next is null && create) {
                Directory.CreateDirectory(nextPath);
                next = OpenWindows(nextPath, directory: true, create: false, write: false);
            }
        } else { next = OpenLinux(Fd(Handle), segment, directory: true, create, write: false); }
        if (next is null) { return false; }
        m_handles.Add(next);
        m_path = nextPath;
        return true;
    }

    internal FileStream? OpenFile(string name, bool create = false, bool exclusive = false, bool newFile = false) {
        var handle = OperatingSystem.IsWindows()
            ? OpenWindows(Path.Combine(m_path, name), directory: false, create, write: create, exclusive, newFile)
            : OpenLinux(Fd(Handle), name, directory: false, create, write: create, exclusive, newFile);
        return handle is null ? null : new FileStream(handle, create ? FileAccess.ReadWrite : FileAccess.Read);
    }

    internal FileStream AcquireLock(CancellationToken cancellationToken) {
        var started = Environment.TickCount64;
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            try { return OpenFile(".puck-lock", create: true, exclusive: true)!; }
            catch (IOException exception) when (exception.InnerException is Win32Exception native &&
                native.NativeErrorCode is 11 or 32 or 33 && Environment.TickCount64 - started < 5000) { Thread.Sleep(1); }
        }
    }

    internal IEnumerable<string> Entries() => Directory.EnumerateFileSystemEntries(AccessPath).Select(Path.GetFileName)!;

    internal bool IsDirectory(string name) {
        // Attribute hints are never used to authorize an open: every subsequent open checks its handle.
        return (File.GetAttributes(Path.Combine(AccessPath, name)) & FileAttributes.Directory) != 0;
    }

    internal ConfinedDirectory? Child(string name) {
        // Keep the parent capability alive in the caller. Linux must use its descriptor, not its old path.
        if (OperatingSystem.IsWindows()) { return Open(Path.Combine(m_path, name), create: false); }
        var handle = OpenLinux(Fd(Handle), name, directory: true, create: false, write: false);
        if (handle is null) { return null; }
        var child = new ConfinedDirectory { m_path = Path.Combine(m_path, name) };
        child.m_handles.Add(handle);
        return child;
    }

    internal void Publish(string temporary, string name) {
        if (OperatingSystem.IsWindows()) {
            if (!MoveFileEx(Path.Combine(m_path, temporary), Path.Combine(m_path, name), 9)) { throw Error("Cannot publish storage blob"); }
        } else {
            if (RenameAt(Fd(Handle), temporary, Fd(Handle), name) != 0 || Fsync(Fd(Handle)) != 0) { throw Error("Cannot publish storage blob"); }
        }
    }

    internal void RemoveTemporary(string name) {
        if (OperatingSystem.IsWindows()) { File.Delete(Path.Combine(m_path, name)); }
        else if (UnlinkAt(Fd(Handle), name, 0) != 0 && Marshal.GetLastPInvokeError() != 2) { throw Error("Cannot remove storage temporary"); }
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));
    public void Dispose() { for (var i = m_handles.Count - 1; i >= 0; i--) { m_handles[i].Dispose(); } }
}
