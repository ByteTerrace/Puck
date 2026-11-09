using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Confinement;

/// <summary>
/// A directory held open by handle. Every name it resolves is looked up relative to that handle, one component at a
/// time, and a link (a symbolic link, junction or other reparse point) is refused rather than followed, so what it
/// reads, writes, renames and deletes stays below the held directory whatever is renamed or replaced on the paths
/// around it. Renames and removals act on handles, never on paths; the one exception is <see cref="Publish"/> on Windows,
/// which names its two entries by the held directory's path because the documented durable move takes paths.
/// <para>On Windows a held directory is open without <c>FILE_SHARE_DELETE</c>, so no other open can rename or delete
/// it while it is held, and an open that asks for removal fails while anyone else holds it. On Linux a held directory
/// is a descriptor: <see cref="TryHoldShared"/> takes a shared <c>flock</c> on it, and a removal's exclusive lock
/// fails while any shared holder lives. The kernel releases a holder's handle and lock when it exits, a crash
/// included.</para>
/// <para>Windows and Linux x64 are supported; every other platform refuses with
/// <see cref="PlatformNotSupportedException"/>. This capability defends what a caller asks it to do; it is not a sandbox
/// for other code running as the same OS identity.</para>
/// </summary>
public sealed partial class ConfinedDirectory : IDisposable {
    private readonly List<SafeFileHandle> m_handles = [];

    private bool m_removable;
    private string m_path = "";

    private ConfinedDirectory() { }

    /// <summary>Gets the path this directory was reached by, for messages; it is never used to reach the directory
    /// again, and it can be stale once the directory is moved.</summary>
    public string Path => m_path;
    /// <summary>Gets the file system's identity of the held directory.</summary>
    public ConfinedIdentity Identity => (OperatingSystem.IsWindows()
        ? Windows.Identity(handle: Handle)
        : Linux.Identity(stat: Linux.StatOf(handle: Handle)));

    private SafeFileHandle Handle => m_handles[^1];

    /// <summary>Opens a directory by walking every component from its volume root, refusing a link anywhere on the way,
    /// and holds every ancestor for the life of the result.</summary>
    /// <param name="path">The directory's path.</param>
    /// <param name="create">Whether missing components are created.</param>
    /// <returns>The held directory, or <see langword="null"/> when a component is missing and <paramref name="create"/> is
    /// <see langword="false"/>.</returns>
    /// <exception cref="IOException">A component is a link or not a directory, the path is a network path, or a
    /// component cannot be opened.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static ConfinedDirectory? Open(string path, bool create) {
        RequireSupported();
        var full = System.IO.Path.GetFullPath(path: path);
        var root = System.IO.Path.GetPathRoot(path: full)!;
        var directory = new ConfinedDirectory { m_path = root };

        try {
            if (OperatingSystem.IsWindows() && root.StartsWith(comparisonType: StringComparison.Ordinal, value: @"\\")) {
                throw new IOException(message: "Confined access requires a local volume.");
            }
            directory.m_handles.Add(item: (OperatingSystem.IsWindows() ? Windows.OpenPath(path: root) : Linux.OpenPath(path: root)));
            foreach (var segment in full[root.Length..].Split(options: StringSplitOptions.RemoveEmptyEntries, separator: System.IO.Path.DirectorySeparatorChar)) {
                var next = OpenDirectory(create: create, name: segment, parent: directory.Handle);

                if (next is null) { directory.Dispose(); return null; }
                directory.m_handles.Add(item: next);
                directory.m_path = System.IO.Path.Combine(path1: directory.m_path, path2: segment);
            }
            return directory;
        } catch { directory.Dispose(); throw; }
    }
    /// <summary>Opens a directory the host chose, resolving its path as the operating system does (links in the path
    /// itself are followed), and confines everything below it.</summary>
    /// <param name="path">The directory's path.</param>
    /// <param name="create">Whether a missing directory is created first.</param>
    /// <returns>The held directory, or <see langword="null"/> when it is missing and <paramref name="create"/> is
    /// <see langword="false"/>.</returns>
    /// <exception cref="IOException">The path is a network path or cannot be opened as a directory.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static ConfinedDirectory? OpenAnchor(string path, bool create) {
        RequireSupported();
        var full = System.IO.Path.TrimEndingDirectorySeparator(path: System.IO.Path.GetFullPath(path: path));

        var network = (full.StartsWith(comparisonType: StringComparison.Ordinal, value: @"\\") &&
            (!full.StartsWith(comparisonType: StringComparison.Ordinal, value: @"\\?\") || full.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: @"\\?\UNC\")));

        if (OperatingSystem.IsWindows() && network) {
            throw new IOException(message: "Confined access requires a local volume.");
        }
        if (create) { _ = Directory.CreateDirectory(path: full); }
        try {
            var directory = new ConfinedDirectory { m_path = full };

            directory.m_handles.Add(item: (OperatingSystem.IsWindows() ? Windows.OpenPath(path: full) : Linux.OpenPath(path: full)));
            return directory;
        } catch (DirectoryNotFoundException) when (!create) {
            return null;
        }
    }
    /// <summary>Reports whether <paramref name="path"/> lies in, or would be created in, the directory
    /// <paramref name="root"/>, by file identity rather than by spelling: a <c>\\?\</c> path, an 8.3 name, a link or a
    /// case-folding mount that names the same directory still counts. Each existing ancestor of
    /// <paramref name="path"/> (itself included when it is a directory) is compared with the root's identity, so a
    /// path that does not exist yet is judged by its deepest existing ancestor.</summary>
    /// <param name="root">The directory that may contain the path; a missing root contains nothing.</param>
    /// <param name="path">The file or directory to place, which need not exist.</param>
    /// <returns><see langword="true"/> when the root is the path or one of its ancestors.</returns>
    /// <exception cref="IOException">The root or an ancestor is a network path or cannot be opened.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static bool Contains(string root, string path) {
        RequireSupported();
        var rootFull = System.IO.Path.TrimEndingDirectorySeparator(path: System.IO.Path.GetFullPath(path: root));
        var full = System.IO.Path.GetFullPath(path: path);

        if (System.IO.Path.GetDirectoryName(path: rootFull) is null) {
            return string.Equals(a: System.IO.Path.GetPathRoot(path: full), b: System.IO.Path.GetPathRoot(path: rootFull), comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        using var held = OpenAnchor(create: false, path: rootFull);

        if (held is null) { return false; }
        var identity = held.Identity;

        // A volume root is never compared: the root here is not one, so no ancestor at that level can be it.
        for (var current = full; (System.IO.Path.GetDirectoryName(path: current) is not null); current = System.IO.Path.GetDirectoryName(path: current)!) {
            if (!Directory.Exists(path: current)) { continue; }
            using var candidate = OpenAnchor(create: false, path: current);

            if ((candidate is not null) && (candidate.Identity == identity)) { return true; }
        }
        return false;
    }
    /// <summary>Opens a child directory relative to this one, holding only the child.</summary>
    /// <param name="name">The child's name: one component, never a path.</param>
    /// <param name="create">Whether a missing child is created.</param>
    /// <returns>The held child, or <see langword="null"/> when it is missing and <paramref name="create"/> is
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a single component.</exception>
    /// <exception cref="IOException">The child is a link or not a directory, or cannot be opened.</exception>
    public ConfinedDirectory? Child(string name, bool create = false) {
        RequireName(name: name);
        var handle = OpenDirectory(create: create, name: name, parent: Handle);

        return ((handle is null) ? null : Wrap(handle: handle, name: name, removable: false));
    }
    /// <summary>Opens a child directory for removal: on Windows with delete access, which no other holder's sharing
    /// mode allows; on Linux with an exclusive lock, which no shared holder allows.</summary>
    /// <param name="name">The child's name: one component, never a path.</param>
    /// <returns>The child, held for removal, or <see langword="null"/> when another process holds it.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a single component.</exception>
    /// <exception cref="DirectoryNotFoundException">The child does not exist.</exception>
    /// <exception cref="IOException">The child is a link or not a directory, or cannot be opened.</exception>
    public ConfinedDirectory? TryOpenChildForRemoval(string name) {
        RequireName(name: name);
        if (OperatingSystem.IsWindows()) {
            var handle = Windows.Open(access: Windows.DirectoryAccess | Windows.Delete | Windows.WriteAttributes, busyIsNull: true, create: false, directory: true,
                name: name, newFile: false, parent: Handle, share: Windows.ShareRead | Windows.ShareWrite);

            if (handle is null) {
                return (Exists(name: name) ? null : throw new DirectoryNotFoundException(message: $"'{name}' does not exist."));
            }
            return Wrap(handle: handle, name: name, removable: true);
        }
        var child = (Linux.Open(create: false, directory: true, name: name, newFile: false, parent: Handle, write: false)
            ?? throw new DirectoryNotFoundException(message: $"'{name}' does not exist."));

        try {
            if (!Linux.TryLock(handle: child, operation: Linux.LockExclusive)) { child.Dispose(); return null; }
            return Wrap(handle: child, name: name, removable: true);
        } catch { child.Dispose(); throw; }
    }
    /// <summary>Registers this handle as a holder of the directory: on Linux a shared lock, which keeps every removal's
    /// exclusive lock out until this handle closes; on Windows the held handle already denies removal.</summary>
    /// <returns><see langword="true"/> when the directory is held; <see langword="false"/> when a removal holds it.</returns>
    /// <exception cref="IOException">The lock cannot be taken for another reason.</exception>
    public bool TryHoldShared() => (OperatingSystem.IsWindows() || Linux.TryLock(handle: Handle, operation: Linux.LockShared));
    /// <summary>Opens a regular, singly linked file relative to this directory without following a link.</summary>
    /// <param name="name">The file's name: one component, never a path.</param>
    /// <param name="create">Whether a missing file is created; the stream is then writable.</param>
    /// <param name="exclusive">Whether the open excludes every other: no other open on Windows, an exclusive
    /// <c>flock</c> on Linux.</param>
    /// <param name="newFile">Whether the file must not already exist.</param>
    /// <returns>The stream, which owns the handle, or <see langword="null"/> when the file is missing and
    /// <paramref name="create"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a single component.</exception>
    /// <exception cref="IOException">The entry is a link, hard-linked, not a regular file, busy, or cannot be
    /// opened.</exception>
    public FileStream? OpenFile(string name, bool create = false, bool exclusive = false, bool newFile = false) {
        RequireName(name: name);
        SafeFileHandle? handle;

        if (OperatingSystem.IsWindows()) {
            handle = Windows.Open(access: Windows.GenericRead | (create ? Windows.GenericWrite : 0), busyIsNull: false, create: (create || newFile),
                directory: false, name: name, newFile: newFile, parent: Handle, share: (exclusive ? 0 : Windows.ShareRead));
        } else {
            handle = Linux.Open(create: (create || newFile), directory: false, name: name, newFile: newFile, parent: Handle, write: create);
            if ((handle is not null) && exclusive && !Linux.TryLock(handle: handle, operation: Linux.LockExclusive)) {
                handle.Dispose();
                throw new IOException(message: $"'{name}' is locked by another holder.", innerException: new System.ComponentModel.Win32Exception(error: Linux.WouldBlock));
            }
        }
        return ((handle is null) ? null : new FileStream(access: (create ? FileAccess.ReadWrite : FileAccess.Read), handle: handle));
    }
    /// <summary>Takes an exclusive lock on a lock file directly in this directory, creating it when missing. The lock
    /// file is never removed, so every holder locks the same file, and the holder's exit releases the lock.</summary>
    /// <param name="name">The lock file's name.</param>
    /// <returns>The held lock, which the caller disposes to release, or <see langword="null"/> when another holder
    /// has it.</returns>
    /// <exception cref="IOException">The lock file is a link or cannot be opened.</exception>
    public FileStream? TryAcquireLock(string name) {
        try {
            return OpenFile(create: true, exclusive: true, name: name)!;
        } catch (IOException exception) when (IsBusy(exception: exception)) {
            return null;
        }
    }
    /// <summary>Waits for <see cref="TryAcquireLock"/> to succeed.</summary>
    /// <param name="name">The lock file's name.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until the lock is free or
    /// <paramref name="cancellationToken"/> fires.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The held lock.</returns>
    /// <exception cref="IOException">The wait timed out, or the lock file is a link or cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public FileStream AcquireLock(string name, TimeSpan timeout, CancellationToken cancellationToken) {
        var started = Environment.TickCount64;
        var delay = 1;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAcquireLock(name: name) is { } held) { return held; }
            if ((timeout != Timeout.InfiniteTimeSpan) && ((Environment.TickCount64 - started) >= ((long)timeout.TotalMilliseconds))) {
                throw new IOException(message: $"'{name}' stayed locked by another holder for {timeout.TotalSeconds:0.#} s.");
            }
            _ = cancellationToken.WaitHandle.WaitOne(millisecondsTimeout: delay);
            delay = Math.Min(val1: 50, val2: (delay * 2));
        }
    }
    /// <summary>Lists this directory's entries through its handle, never through a path.</summary>
    /// <returns>Each entry and what it was when listed.</returns>
    /// <exception cref="IOException">The directory cannot be listed.</exception>
    public IReadOnlyList<ConfinedEntry> Entries() => (OperatingSystem.IsWindows() ? Windows.Entries(handle: Handle) : Linux.Entries(handle: Handle));
    /// <summary>Finds a link anywhere below this directory, through handles.</summary>
    /// <returns>The first link's path relative to this directory, with forward slashes, or <see langword="null"/> when
    /// none exists.</returns>
    /// <exception cref="IOException">An entry cannot be inspected.</exception>
    public string? FindLink() {
        // Depth first without recursion: one open directory per level, and no stack, whatever the tree's depth.
        var levels = new Stack<(ConfinedDirectory? Directory, string Path, Queue<ConfinedEntry> Remaining)>();

        levels.Push(item: (null, "", new Queue<ConfinedEntry>(collection: Entries())));
        try {
            while (levels.TryPeek(result: out var level)) {
                if (!level.Remaining.TryDequeue(result: out var entry)) {
                    level.Directory?.Dispose();
                    _ = levels.Pop();
                    continue;
                }
                var path = ((level.Path.Length == 0) ? entry.Name : $"{level.Path}/{entry.Name}");

                if (entry.Kind == ConfinedEntryKind.Link) { return path; }
                if ((entry.Kind == ConfinedEntryKind.Directory) && ((level.Directory ?? this).Child(name: entry.Name) is { } child)) {
                    levels.Push(item: (child, path, new Queue<ConfinedEntry>()));
                    foreach (var grandchild in child.Entries()) { levels.Peek().Remaining.Enqueue(item: grandchild); }
                }
            }
            return null;
        } finally {
            foreach (var level in levels) { level.Directory?.Dispose(); }
        }
    }
    /// <summary>Atomically and durably replaces <paramref name="name"/> with the file <paramref name="temporary"/>, both
    /// directly in this directory: when it returns, a power loss leaves the replacement; when it throws the replaced file
    /// is as it was. The temporary's content must already be flushed by its writer (<see cref="FileStream.Flush(bool)"/>
    /// with <c>flushToDisk: true</c>); this call makes the rename itself durable. This is the one durable replacement
    /// every caller uses: <see cref="AtomicFile.WriteAllBytesDurably"/> and the blob store both end here.
    /// <para>On Windows the temporary is first opened through the held handle without following a link, and refused when
    /// it is a link, hard-linked or not a regular file; it is then moved with <c>MoveFileExW</c>
    /// (<c>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH</c>), whose documentation states that it does not return
    /// until the move is on the disk (Microsoft Learn, <c>MoveFileExW</c>, winbase.h). That call takes paths, so this
    /// directory's <see cref="Path"/> reaches it: the held directory, and every ancestor <see cref="Open"/> holds, are open
    /// without <c>FILE_SHARE_DELETE</c>, so none can be renamed or deleted, and the path cannot name another directory,
    /// while this object lives. A directory from <see cref="OpenAnchor"/> holds only itself, and its own path is the
    /// host's. On Linux it is <c>renameat</c> relative to the held directory followed by <see cref="Flush"/>, the
    /// <c>fsync</c> of the directory that fsync(2) requires to persist the entry, and the rename is atomic per
    /// rename(2).</para></summary>
    /// <param name="temporary">The replacing file's name: one component, never a path.</param>
    /// <param name="name">The replaced file's name: one component, never a path.</param>
    /// <exception cref="ArgumentException">Either name is not a single component.</exception>
    /// <exception cref="IOException">The temporary does not exist, or on Windows is a link, hard-linked or not a regular
    /// file, or the replacement or the directory flush fails.</exception>
    public void Publish(string temporary, string name) {
        RequireName(name: temporary);
        RequireName(name: name);
        if (OperatingSystem.IsWindows()) {
            // Verified here, held across the move: the share mode lets the move open the same file for its own rename.
            using var source = (Windows.Open(access: Windows.ReadAttributes, busyIsNull: false, create: false, directory: false, name: temporary, newFile: false,
                parent: Handle, share: (Windows.ShareRead | Windows.ShareWrite | Windows.ShareDelete)) ?? throw new FileNotFoundException(message: $"'{temporary}' does not exist."));

            Windows.MoveDurably(destination: System.IO.Path.Combine(path1: m_path, path2: name), source: System.IO.Path.Combine(path1: m_path, path2: temporary));
        } else {
            Linux.RenameReplace(parent: Handle, sourceName: temporary, destinationName: name);
            Linux.Flush(directory: Handle);
        }
    }
    /// <summary>Makes this directory's entries durable: on Linux an <c>fsync</c> of the directory, which persists a rename
    /// into it or a file created in it (fsync(2) requires this beside the file's own flush). On Windows it does nothing:
    /// a file's own <c>FlushFileBuffers</c> and a write-through move (<see cref="Publish"/>) are the documented durability
    /// operations there, and a directory opened for listing cannot be flushed.</summary>
    /// <exception cref="IOException">The directory cannot be flushed.</exception>
    public void Flush() {
        if (!OperatingSystem.IsWindows()) { Linux.Flush(directory: Handle); }
    }
    /// <summary>Makes every directory below this one, and this one, durable (<see cref="Flush"/>), through handles and
    /// without recursion; a link is never followed.</summary>
    /// <exception cref="IOException">A directory cannot be opened or flushed.</exception>
    public void FlushTree() {
        if (OperatingSystem.IsWindows()) { return; }
        var pending = new Stack<ConfinedDirectory>();

        try {
            Flush();
            foreach (var entry in Entries()) {
                if ((entry.Kind == ConfinedEntryKind.Directory) && (Child(name: entry.Name) is { } child)) { pending.Push(item: child); }
            }
            while (pending.TryPop(result: out var directory)) {
                using (directory) {
                    directory.Flush();
                    foreach (var entry in directory.Entries()) {
                        if ((entry.Kind == ConfinedEntryKind.Directory) && (directory.Child(name: entry.Name) is { } child)) { pending.Push(item: child); }
                    }
                }
            }
        } finally {
            foreach (var directory in pending) { directory.Dispose(); }
        }
    }
    /// <summary>Removes the file or directory tree <paramref name="name"/> directly in this directory, through handles:
    /// every step opens an entry relative to its open directory and never follows a link. A link anywhere in the tree
    /// refuses the removal there, leaving what is not yet removed in place.</summary>
    /// <param name="name">The entry's name: one component, never a path.</param>
    /// <returns><see langword="true"/> when an entry was removed; <see langword="false"/> when none existed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a single component.</exception>
    /// <exception cref="IOException">The entry or one below it is a link, is held by another process, or cannot be
    /// removed.</exception>
    public bool DeleteChild(string name) {
        RequireName(name: name);
        return RemoveEntry(name: name, parent: Handle);
    }
    /// <summary>Moves this directory, held for removal, to <paramref name="destination"/>/<paramref name="newName"/>. On
    /// Windows the held object itself moves; on Linux the move is made by name relative to <paramref name="parent"/>'s
    /// handle, which names this directory unless another process of the same user renames entries meanwhile.</summary>
    /// <param name="parent">The directory this one is in.</param>
    /// <param name="name">This directory's current name in <paramref name="parent"/>.</param>
    /// <param name="destination">The directory to move into, on the same volume.</param>
    /// <param name="newName">A name that does not exist in <paramref name="destination"/>.</param>
    /// <exception cref="InvalidOperationException">This directory was not opened by
    /// <see cref="TryOpenChildForRemoval"/>.</exception>
    /// <exception cref="IOException">The move fails.</exception>
    public void MoveTo(ConfinedDirectory parent, string name, ConfinedDirectory destination, string newName) {
        ArgumentNullException.ThrowIfNull(argument: parent);
        ArgumentNullException.ThrowIfNull(argument: destination);
        RequireName(name: name);
        RequireName(name: newName);
        RequireRemovable();
        if (OperatingSystem.IsWindows()) {
            Windows.Rename(destination: destination.Handle, handle: Handle, name: newName);
        } else {
            Linux.Rename(destinationName: newName, destinationParent: destination.Handle, sourceName: name, sourceParent: parent.Handle);
        }
        m_path = System.IO.Path.Combine(path1: destination.m_path, path2: newName);
    }
    /// <summary>Removes everything below this directory, held for removal, and then the directory itself, which is
    /// <paramref name="name"/> in <paramref name="parent"/>. Every step acts through handles and never follows a link;
    /// a link refuses the removal there.</summary>
    /// <param name="parent">The directory this one is in.</param>
    /// <param name="name">This directory's current name in <paramref name="parent"/>.</param>
    /// <exception cref="InvalidOperationException">This directory was not opened by
    /// <see cref="TryOpenChildForRemoval"/>.</exception>
    /// <exception cref="IOException">An entry below is a link or is held, or a removal fails; what was not yet removed
    /// stays in place.</exception>
    public void DeleteTree(ConfinedDirectory parent, string name) {
        ArgumentNullException.ThrowIfNull(argument: parent);
        RequireName(name: name);
        RequireRemovable();
        RemoveContents(directory: Handle);
        if (OperatingSystem.IsWindows()) {
            Windows.Remove(handle: Handle);
        } else {
            _ = Linux.Unlink(directory: true, name: name, parent: parent.Handle);
        }
    }

    /// <summary>Removes the directory at <paramref name="path"/> and everything below it, the one way the repository
    /// deletes a tree it owns. The directory is reached as the operating system resolves its path (links in the path
    /// itself are followed), then removed by <see cref="DeleteTree"/>: iteratively, one open handle per level, so a
    /// tree of any depth cannot overflow the stack, and never through a link.</summary>
    /// <param name="path">The directory to remove.</param>
    /// <exception cref="IOException">The directory or an entry below it is a link or is held by another process, or
    /// cannot be removed, or <paramref name="path"/> is a volume root; what was not yet removed stays in place. A
    /// directory that does not exist is not an error.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static void RemoveTree(string path) {
        var full = System.IO.Path.TrimEndingDirectorySeparator(path: System.IO.Path.GetFullPath(path: path));
        var name = System.IO.Path.GetFileName(path: full);

        if (!Directory.Exists(path: full)) {
            return;
        }
        if ((System.IO.Path.GetDirectoryName(path: full) is not { } parentPath) || (name.Length == 0)) {
            throw new IOException(message: $"'{full}' is a volume root, which a tree removal never owns.");
        }

        using var parent = OpenAnchor(
            create: false,
            path: parentPath
        );

        if (parent is null) {
            return;
        }

        ConfinedDirectory? directory;

        try {
            directory = parent.TryOpenChildForRemoval(name: name);
        } catch (DirectoryNotFoundException) {
            return;
        }

        if (directory is null) {
            throw new IOException(message: $"'{full}' is held by another process.");
        }

        using (directory) {
            directory.DeleteTree(
                name: name,
                parent: parent
            );
        }
    }

    // The base address of this process's executable image on Windows (GetModuleHandle of null), for a single-file app
    // whose executable is its image; zero elsewhere.
    internal static nint ProcessImageAddress() => (OperatingSystem.IsWindows() ? Windows.GetModuleHandle(name: 0) : 0);

    /// <summary>Reports whether the regular file <paramref name="name"/> in this directory is the file that backs a
    /// memory mapping of this process. On Linux any mapping with the file's inode and current path matches, both as the
    /// kernel reports them. On Windows the mapping that contains <paramref name="address"/> matches when the name its
    /// file was opened by (the only name Windows reports for a mapping) now names the held file, compared by file
    /// identity; a loaded .NET assembly's file stays open for the life of its process, and an open file denies renaming
    /// or deleting its directory, so that directory cannot have been replaced since the file was opened.</summary>
    /// <param name="name">The file's name: one component, never a path.</param>
    /// <param name="address">An address inside the mapping on Windows; ignored on Linux.</param>
    /// <returns><see langword="true"/> when the file backs that mapping.</returns>
    /// <exception cref="IOException">The file cannot be inspected.</exception>
    public bool IsMapped(string name, nint address) {
        RequireName(name: name);
        if (!OperatingSystem.IsWindows()) { return Linux.IsMapped(name: name, parent: Handle); }
        using var file = Windows.Open(access: Windows.ReadAttributes, busyIsNull: false, create: false, directory: false, name: name, newFile: false,
            parent: Handle, share: Windows.ShareRead | Windows.ShareWrite | Windows.ShareDelete);

        if ((file is null) || (Windows.MappedNtPath(address: address) is not { } mapped)) { return false; }
        using var opened = Windows.OpenNtPath(ntPath: mapped);

        return ((opened is not null) && (Windows.Identity(handle: opened) == Windows.Identity(handle: file)));
    }
    /// <summary>Closes every held handle, which releases every lock this object took.</summary>
    public void Dispose() {
        for (var i = (m_handles.Count - 1); (i >= 0); i--) { m_handles[i].Dispose(); }
        m_handles.Clear();
    }

    private static SafeFileHandle? OpenDirectory(SafeFileHandle parent, string name, bool create) => (OperatingSystem.IsWindows()
        ? Windows.Open(access: Windows.DirectoryAccess, busyIsNull: false, create: create, directory: true, name: name, newFile: false,
            parent: parent, share: Windows.ShareRead | Windows.ShareWrite)
        : Linux.Open(create: create, directory: true, name: name, newFile: false, parent: parent, write: false));
    private ConfinedDirectory Wrap(SafeFileHandle handle, string name, bool removable) {
        var child = new ConfinedDirectory { m_path = System.IO.Path.Combine(path1: m_path, path2: name), m_removable = removable };

        child.m_handles.Add(item: handle);
        return child;
    }
    private bool Exists(string name) => Entries().Any(predicate: entry => string.Equals(a: entry.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase));
    private void RequireRemovable() {
        if (!m_removable) { throw new InvalidOperationException(message: "The directory was not opened for removal."); }
    }
    private static bool IsBusy(IOException exception) => (OperatingSystem.IsWindows() ? Windows.IsBusy(exception: exception) : Linux.IsBusy(exception: exception));
    private static void RequireSupported() {
        if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && (RuntimeInformation.ProcessArchitecture == Architecture.X64))) {
            throw new PlatformNotSupportedException(message: "Confined access supports Windows and Linux x64.");
        }
    }
    private static void RequireName(string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        if ((name is "." or "..") || (name.IndexOfAny(anyOf: ['/', '\\', '\0']) >= 0) || (OperatingSystem.IsWindows() && name.Contains(value: ':'))) {
            throw new ArgumentException(message: $"'{name}' is not a single directory entry name.", paramName: nameof(name));
        }
    }
    private static IReadOnlyList<ConfinedEntry> EntriesOf(SafeFileHandle directory) => (OperatingSystem.IsWindows()
        ? Windows.Entries(handle: directory)
        : Linux.Entries(handle: directory));
    // Removes everything below a directory, depth first and without recursion, so a tree of any depth costs one open
    // handle per level and no stack. The caller keeps the directory itself and its handle.
    private static void RemoveContents(SafeFileHandle directory) {
        var levels = new Stack<(SafeFileHandle Directory, SafeFileHandle? Parent, string? Name, Queue<string> Remaining)>();

        levels.Push(item: (directory, null, null, new Queue<string>(collection: EntriesOf(directory: directory).Select(selector: static entry => entry.Name))));
        try {
            while (levels.TryPeek(result: out var level)) {
                if (level.Remaining.TryDequeue(result: out var name)) {
                    if (OpenForRemoval(existed: out _, name: name, parent: level.Directory) is { } child) {
                        levels.Push(item: (child, level.Directory, name, new Queue<string>()));
                        foreach (var entry in EntriesOf(directory: child)) { levels.Peek().Remaining.Enqueue(item: entry.Name); }
                    }
                    continue;
                }
                _ = levels.Pop();
                if (level.Name is not null) { RemoveEmptied(directory: level.Directory, name: level.Name, parent: level.Parent!); }
            }
        } finally {
            foreach (var level in levels) {
                if (level.Name is not null) { level.Directory.Dispose(); }
            }
        }
    }
    // One step of a removal: the entry is opened relative to its parent's handle without following it and inspected
    // through its own handle. A file is removed at once (through its handle on Windows, by name relative to the parent
    // after the same no-follow inspection on Linux); a directory's handle is returned for emptying.
    private static SafeFileHandle? OpenForRemoval(SafeFileHandle parent, string name, out bool existed) {
        existed = false;
        if (OperatingSystem.IsWindows()) {
            var handle = Windows.OpenAny(access: Windows.Delete | Windows.ListDirectory | Windows.ReadAttributes | Windows.WriteAttributes, isDirectory: out var isDirectory,
                isLink: out var isLink, name: name, parent: parent, share: Windows.ShareRead | Windows.ShareWrite);

            if (handle is null) { return null; }
            existed = true;
            if (isDirectory && !isLink) { return handle; }
            using (handle) {
                if (isLink) { throw new IOException(message: $"'{name}' is a link; a confined removal never deletes through one and left it in place."); }
                Windows.Remove(handle: handle);
                return null;
            }
        }
        var directory = Linux.OpenForRemoval(directory: true, error: out var error, name: name, parent: parent, writeOnly: false);

        if (directory is not null) {
            try {
                RequireRemovalLock(handle: directory, name: name);
                existed = true;
                return directory;
            } catch { directory.Dispose(); throw; }
        }
        if (error == Linux.NoEntry) { return null; }
        if (error is not (Linux.NotDirectory or Linux.Loop)) { throw Linux.Error(code: error, message: $"Cannot open confined entry '{name}'"); }
        if (Linux.StatAt(name: name, parent: parent) is not { } stat) { return null; }
        if (Linux.Kind(stat: stat) == ConfinedEntryKind.Link) {
            throw new IOException(message: $"'{name}' is a link; a confined removal never deletes through one and left it in place.");
        }
        if (Linux.Kind(stat: stat) == ConfinedEntryKind.File) {
            // A file's cooperating holder locks the file, not its parent directory. Keep its descriptor and
            // exclusive lock through unlink; hard links are safe to unlink without changing their attributes. A file
            // this user can open neither to read nor to write cannot be locked; it is unlinked as before the lock.
            var file = Linux.OpenForRemoval(directory: false, error: out error, name: name, parent: parent, writeOnly: false);

            if ((file is null) && (error == Linux.AccessDenied)) {
                file = Linux.OpenForRemoval(directory: false, error: out error, name: name, parent: parent, writeOnly: true);
                if ((file is null) && (error == Linux.AccessDenied)) {
                    existed = Linux.Unlink(directory: false, name: name, parent: parent);
                    return null;
                }
            }
            if (file is null) {
                if (error == Linux.NoEntry) { return null; }
                throw Linux.Error(code: error, message: $"Cannot open confined entry '{name}'");
            }
            using var opened = file;

            if (Linux.Kind(stat: Linux.StatOf(handle: file)) != ConfinedEntryKind.File) {
                throw new IOException(message: $"'{name}' is no longer a regular file; confined removal refuses it.");
            }
            RequireRemovalLock(handle: file, name: name);
            existed = Linux.Unlink(directory: false, name: name, parent: parent);
            return null;
        }
        existed = Linux.Unlink(directory: false, name: name, parent: parent);
        return null;
    }
    private static void RequireRemovalLock(SafeFileHandle handle, string name) {
        if (!Linux.TryLock(handle: handle, operation: Linux.LockExclusive)) {
            throw Linux.Error(code: Linux.WouldBlock, message: $"'{name}' is held by another process.");
        }
    }
    // Removes a directory emptied through its handle, and closes that handle either way.
    private static void RemoveEmptied(SafeFileHandle directory, SafeFileHandle parent, string name) {
        using (directory) {
            if (OperatingSystem.IsWindows()) {
                Windows.Remove(handle: directory);
                return;
            }
        }
        _ = Linux.Unlink(directory: true, name: name, parent: parent);
    }
    private static bool RemoveEntry(SafeFileHandle parent, string name) {
        if (OpenForRemoval(existed: out var existed, name: name, parent: parent) is { } directory) {
            try {
                RemoveContents(directory: directory);
            } catch {
                directory.Dispose();
                throw;
            }
            RemoveEmptied(directory: directory, name: name, parent: parent);
        }
        return existed;
    }
}
