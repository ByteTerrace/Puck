namespace Puck.Abstractions;

/// <summary>
/// The one advisory lock file: an open handle on a file that other processes try to open the same way. The operating
/// system releases the lock with the handle however its holder ends, so a crashed process leaves nothing to clean up. A
/// lock file is never deleted while a holder might need it, since removing it lets two processes lock different files
/// at one path.
/// <para>Only another holder counts as contention: a sharing violation on Windows, the platform's <c>EWOULDBLOCK</c>
/// from <c>flock</c> on Unix. Any other failure (a path that cannot be created, a full disk, a refused permission) is
/// not another holder, and surfaces at once as the exception that names it rather than waiting out a deadline.</para>
/// </summary>
public static class FileLock {
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(value: 50);

    /// <summary>Whether an exception from opening a lock file means another holder has it.</summary>
    /// <param name="exception">The failure from opening the file.</param>
    /// <returns><see langword="true"/> for ERROR_SHARING_VIOLATION on Windows, which reports it as an HRESULT, and
    /// for <c>EWOULDBLOCK</c> elsewhere, which .NET reports as the raw errno: 35 on Darwin and FreeBSD, 11 on
    /// Linux.</returns>
    public static bool IsContention(IOException exception) {
        ArgumentNullException.ThrowIfNull(argument: exception);

        return (OperatingSystem.IsWindows()
            ? (exception.HResult == unchecked((int)0x80070020))
            : (exception.HResult == ((OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) ? 35 : 11)));
    }
    /// <summary>Takes the exclusive lock on <paramref name="path"/> without waiting, creating the file and its parent
    /// directory when missing.</summary>
    /// <param name="path">The lock file.</param>
    /// <returns>The held lock, released when disposed; or <see langword="null"/> when another holder has it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for a reason other than another holder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file or its directory cannot be created or opened for
    /// writing.</exception>
    public static FileStream? TryAcquire(string path) =>
        Open(
            access: FileAccess.ReadWrite,
            path: path,
            share: FileShare.None
        );
    /// <summary>Takes the exclusive lock on an existing <paramref name="path"/> without waiting, and deletes the file
    /// when the lock is disposed, so the file is removed while no other process can hold it: a run that opens the path
    /// afterwards creates a new file, and one that tries meanwhile finds the lock held.</summary>
    /// <param name="path">The lock file.</param>
    /// <returns>The held lock, whose disposal deletes the file; or <see langword="null"/> when the file does not exist
    /// or another holder has it.</returns>
    /// <exception cref="IOException">The lock file cannot be opened for a reason other than another holder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file cannot be opened for writing or deleted.</exception>
    public static FileStream? TryAcquireForDeletion(string path) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        try {
            return new FileStream(
                access: FileAccess.ReadWrite,
                mode: FileMode.Open,
                bufferSize: 1,
                options: FileOptions.DeleteOnClose,
                path: path,
                share: FileShare.None
            );
        } catch (FileNotFoundException) {
            return null;
        } catch (DirectoryNotFoundException) {
            return null;
        } catch (IOException exception) when (IsContention(exception: exception)) {
            return null;
        }
    }
    /// <summary>Takes a shared hold on <paramref name="path"/> without waiting: any number of shared holders coexist,
    /// and none can coexist with an exclusive holder. The file is opened read-only with a shared mode, which .NET
    /// turns into a shared advisory lock on Unix.</summary>
    /// <param name="path">The lock file.</param>
    /// <returns>The held lock, released when disposed; or <see langword="null"/> when an exclusive holder has it.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for a reason other than an exclusive holder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file or its directory cannot be created or opened.</exception>
    public static FileStream? TryAcquireShared(string path) =>
        Open(
            access: FileAccess.Read,
            path: path,
            share: FileShare.ReadWrite
        );
    /// <summary>Waits for the exclusive lock on <paramref name="path"/>, polling while another process holds it.</summary>
    /// <param name="path">The lock file.</param>
    /// <param name="expired">Whether the wait has run out, asked before each wait; <see langword="null"/> waits until
    /// the lock is taken or the wait is canceled. The lock is tried once before it is first asked.</param>
    /// <param name="waiting">Called once, synchronously, when the first attempt finds another holder.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>The held lock, released when disposed; or <see langword="null"/> when <paramref name="expired"/> said
    /// so first.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while another
    /// holder held the lock.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for a reason other than another holder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file or its directory cannot be created or opened for
    /// writing.</exception>
    public static FileStream? Acquire(string path, Func<bool>? expired = null, Action? waiting = null, CancellationToken cancellationToken = default) {
        var announced = false;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryAcquire(path: path) is { } held) {
                return held;
            }
            if (!announced) {
                announced = true;
                waiting?.Invoke();
            }
            if (expired?.Invoke() ?? false) {
                return null;
            }

            _ = cancellationToken.WaitHandle.WaitOne(timeout: PollInterval);
        }
    }
    /// <summary>Waits, without blocking a thread, for the exclusive lock on <paramref name="path"/>.</summary>
    /// <param name="path">The lock file.</param>
    /// <param name="expired">Whether the wait has run out, asked before each wait; <see langword="null"/> waits until
    /// the lock is taken or the wait is canceled.</param>
    /// <param name="waiting">Called once, synchronously, when the first attempt finds another holder.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>The held lock, released when disposed; or <see langword="null"/> when <paramref name="expired"/> said
    /// so first.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while another
    /// holder held the lock.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for a reason other than another holder.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file or its directory cannot be created or opened for
    /// writing.</exception>
    public static async Task<FileStream?> AcquireAsync(string path, Func<bool>? expired = null, Action? waiting = null, CancellationToken cancellationToken = default) {
        var announced = false;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryAcquire(path: path) is { } held) {
                return held;
            }
            if (!announced) {
                announced = true;
                waiting?.Invoke();
            }
            if (expired?.Invoke() ?? false) {
                return null;
            }

            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: PollInterval
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private static FileStream? Open(string path, FileAccess access, FileShare share) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        if (Path.GetDirectoryName(path: Path.GetFullPath(path: path)) is { Length: > 0 } directory) {
            _ = Directory.CreateDirectory(path: directory);
        }

        try {
            return new FileStream(
                access: access,
                mode: FileMode.OpenOrCreate,
                path: path,
                share: share
            );
        } catch (IOException exception) when (IsContention(exception: exception)) {
            return null;
        }
    }
}
