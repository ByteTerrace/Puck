# Puck.Confinement

Puck.Confinement holds a directory open by handle and resolves every name below
it relative to that handle, one component at a time, refusing a link (a symbolic
link, a junction or any other reparse point) instead of following it. Reads,
writes, renames and deletions act on handles, never on a path checked earlier,
so what a caller reaches stays below the held directory whatever is renamed or
replaced on the paths around it. It references only the BCL, so the launcher
stub can use it without the rest of the engine.

## Atomic replacement

`AtomicFile` provides flushed temp-then-rename file replacement and reads that
share delete access with concurrent publication. It is the shared writer for
the launcher stub and updater, without an engine dependency. Unlike
`ConfinedDirectory`, it operates on trusted paths rather than held directories.

## Usage

`ConfinedDirectory.Open` walks a path from its volume root and refuses a link in
any component; `Puck.Storage` uses it for blob storage and host configuration
reads. `ConfinedDirectory.OpenAnchor` opens a directory the host chose as the
operating system resolves it and confines everything below; the launcher uses it
for an install root. From a held directory:

- `Child`, `OpenFile` and `Entries` open and list relative to the handle.
- `TryAcquireLock` and `AcquireLock` take a persistent lock file's exclusive lock.
- `TryHoldShared` registers the handle as a holder of its directory, and
  `TryOpenChildForRemoval` opens a child for removal, which fails while any other
  process holds it.
- `MoveTo` moves a directory held for removal into another held directory, and
  `DeleteTree` and `DeleteChild` remove a tree step by step through handles; a
  link anywhere refuses the removal there.
- `IsMapped` reports whether a file in the directory backs one of this process's
  memory mappings, which the launcher uses to confirm the version directory a
  process was loaded from.
- `Identity` is the file system's identity of the held directory, for comparing
  two handles whatever paths reached them.

## Install registration

`InstallRegistration` and `InstallLayout` hold the install root's process
registration here, because every entry point an installed payload carries, the
stub and the launcher must share it and this is the one BCL-only project all of
them reach. `TryRegisterProcess` is the first statement of each such entry point:
a process whose own directory sits directly under a `versions/` directory
belongs to the install root above it, whether or not that root has written
`current` yet. It waits for `update.lock`, confirms by identity that
`versions/<name>` is the directory its image (the entry assembly's file, or the
executable of a single-file app) was loaded from, and holds it for the life of
the process; one that cannot refuses to start. The launcher's pruner never removes a held version (see the
[launcher README](../Puck.Launcher/README.md#pruning-past-versions)).

## Platforms

Windows and Linux x64 are supported; every other platform refuses with
`PlatformNotSupportedException`.

On Windows, every lookup below a handle is `NtCreateFile` with that handle as
`OBJECT_ATTRIBUTES.RootDirectory` and `FILE_OPEN_REPARSE_POINT`, and a held
directory is open with read-type access and without `FILE_SHARE_DELETE`, so no
other open can rename or delete it while it is held. Renames use
`FileRenameInformation` with a `RootDirectory` through `NtSetInformationFile`
(kernel32's `SetFileInformationByHandle` refuses a rename with a root handle), and
deletions set `FileDispositionInformationEx` with POSIX semantics on the entry's
own handle. Sources: the Windows Driver Kit reference for
[NtCreateFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile),
[FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information)
and
[FILE_DISPOSITION_INFORMATION_EX](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex),
and the Win32 reference for
[GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex).
Measured on Windows 11 (NTFS): a held directory handle that requests only
`FILE_READ_ATTRIBUTES` takes no part in sharing checks, so the handle requests
`FILE_LIST_DIRECTORY`; an open file inside a directory denies renaming the
directory even when that file shares delete; `K32GetMappedFileNameW` reports the
name a mapped file was opened by, not its current name.

Removal handles also request `FILE_WRITE_ATTRIBUTES`. On ReFS, a read-only file
can still return access denied with `FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE`;
the remover clears that attribute through the same held handle and retries.
It refuses to change a hard-linked file's attributes, which would affect names
outside the held tree. The
[Windows disposition contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)
defines the ignore flag; independent implementations in
[Go](https://go.dev/src/internal/syscall/windows/at_windows.go) and
[libuv](https://github.com/libuv/libuv/blob/v1.x/src/win/fs.c) also clear attributes
through handles when their disposition flags are unsupported. Unlike those
fallback conditions, the ReFS case returns access denied with the flag accepted.

On Linux x64, every lookup is `openat`, `fstatat`, `renameat2` or `unlinkat`
relative to a held descriptor with `O_NOFOLLOW` or `AT_SYMLINK_NOFOLLOW`, listing
reads the descriptor's `/proc/self/fd` link, and holding a directory is an
`flock` on its descriptor. Removal takes an exclusive lock on every descendant
directory and regular file as well, refusing a cooperating holder below the
top-level directory before removing that entry. A regular file this user can
open neither to read nor to write cannot be locked, and is unlinked without a
lock. A move and the final removal of an emptied directory
are made by name relative to the held parent's descriptor, which names the held
directory unless another process of the same user renames entries meanwhile; the
launcher's threat model leaves that out
([decision](../../docs/decisions/runtime-and-delivery.md#releases)). A mapping is matched by its inode and current path in `/proc/self/maps`,
because the device number printed there differs from `stat`'s on btrfs
subvolumes. Sources: the
[openat(2)](https://man7.org/linux/man-pages/man2/openat.2.html),
[flock(2)](https://man7.org/linux/man-pages/man2/flock.2.html),
[rename(2)](https://man7.org/linux/man-pages/man2/rename.2.html),
[unlink(2)](https://man7.org/linux/man-pages/man2/unlink.2.html) and
[proc_pid_maps(5)](https://man7.org/linux/man-pages/man5/proc_pid_maps.5.html)
pages.

This capability defends what a caller asks it to do. It is not a sandbox for
other code running as the same OS identity.

## Verification

`RunDirectorySweepLawTests` in `Puck.Cli.Core.Tests` sweeps a 5,000-deep stale
run directory on a 256 KB stack and refuses a directory holding a link out of
the root, which the repository's run-directory policy removes through
`DeleteTree`.
`InstallPruneLawTests` in the [launcher tests](../../tests/Puck.Launcher.Tests/README.md)
and `ConfinedStorageLawTests` in `Puck.World.Server.Tests` exercise real
directories, links, concurrent replacement and held removals on both platforms.

## Documentation

📚 [Engine architecture](../../docs/architecture/README.md) · 🛠️ [Development](../../docs/development/README.md)
