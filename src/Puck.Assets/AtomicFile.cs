using System.Runtime.Versioning;
using System.Text;

namespace Puck.Assets;

/// <summary>
/// Replaces files atomically: new content is written to a fresh temporary file beside the destination, flushed to
/// disk, and renamed over the destination with <see cref="File.Move(string, string, bool)"/>, so neither a reader nor a
/// crash mid-write ever finds a partially written file behind the real name.
/// <para>The temporary file lives in the destination's own directory, so the rename never crosses a volume, and is
/// named <c>&lt;file name&gt;.&lt;random&gt;.tmp</c>, so concurrent writers of one destination never share a temporary. A
/// write that fails at any step deletes its temporary file: a failed write leaves the directory as it found it,
/// except that a missing parent directory is created first. An overwriting <see cref="File.Move(string, string, bool)"/>
/// is a single atomic rename on NTFS and on POSIX file systems alike. Windows refuses that rename while another
/// process holds the old file memory-mapped, so a refused rename over an existing file falls back to
/// <see cref="File.Replace(string, string, string?)"/>, which moves the old file aside and leaves the reader's view on
/// the old bytes. The old file moves to a backup named <c>&lt;file name&gt;.&lt;random&gt;.replaced.tmp</c>, never to a
/// name ReplaceFile chooses, and is deleted once the replace is done; a replace that failed after moving it aside puts
/// it back unless another writer's file has taken the name. A backup that cannot be deleted, an image still mapped,
/// is the one file a write leaves, under that temporary name.</para>
/// <para>A destination spelled in another case than an existing entry on a case-insensitive file system replaces that
/// entry under the name it already carries.</para>
/// <para>On Unix, the replacement keeps the destination's existing file mode unless the caller names a mode for it
/// (a secret written owner-only). On Windows, the replacement is a new file that takes its directory's inherited
/// access control.</para>
/// </summary>
public static class AtomicFile {
    private delegate void Fill<TState>(FileStream stream, TState state) where TState : allows ref struct;

    [UnsupportedOSPlatform(platformName: "windows")]
    private static void KeepUnixMode(FileStream stream, string destination) {
        UnixFileMode mode;

        try {
            mode = File.GetUnixFileMode(path: destination);
        } catch (FileNotFoundException) {
            return;
        }

        File.SetUnixFileMode(
            fileHandle: stream.SafeFileHandle,
            mode: mode
        );
    }
    // On a case-insensitive file system a destination spelled in another case names the existing entry; the
    // replacement keeps the name that entry already carries rather than renaming it to the caller's spelling.
    private static string ExistingSpelling(string path) {
        if (
            !File.Exists(path: path) ||
            (Path.GetDirectoryName(path: path) is not { } directory)
        ) {
            return path;
        }

        foreach (var existing in Directory.EnumerateFiles(
            path: directory,
            searchPattern: Path.GetFileName(path: path)
        )) {
            return existing;
        }

        return path;
    }
    // A replace that moved the destination aside and then failed restores it, unless another writer has put a newer
    // file there since; otherwise the old bytes are no one's and are deleted. A backup that cannot be deleted, an image
    // still mapped, stays under its own temporary name, which a directory's cleanup can find.
    private static void ReleaseBackup(string backup, string destination) {
        try {
            if (!File.Exists(path: destination)) {
                File.Move(
                    destFileName: destination,
                    overwrite: false,
                    sourceFileName: backup
                );

                return;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Another writer's file took the name first; the backup is older than it.
        }

        try {
            File.Delete(path: backup);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // An image still mapped cannot be deleted; its name ends in the temporary extension.
        }
    }
    private static void Replace<TState>(string path, UnixFileMode? unixCreateMode, TState state, Fill<TState> fill) where TState : allows ref struct {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var destination = ExistingSpelling(path: Path.GetFullPath(path: path));
        var directory = Path.GetDirectoryName(path: destination)!;
        var temporary = Path.Combine(
            path1: directory,
            path2: $"{Path.GetFileName(path: destination)}.{Guid.NewGuid():N}.tmp"
        );
        var options = new FileStreamOptions {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
        };

        if (
            (unixCreateMode is { } mode) &&
            !OperatingSystem.IsWindows()
        ) {
            options.UnixCreateMode = mode;
        }

        // The name a replace moves the old destination to, chosen here rather than left to ReplaceFile: its own name for
        // the replaced file (<name>~RF<hex>.TMP) is one nothing could find again, and a replace that fails partway, or
        // lands over a file that cannot be deleted while it is mapped as an image, leaves the replaced file there.
        string? backup = null;

        _ = Directory.CreateDirectory(path: directory);

        try {
            using (var stream = new FileStream(
                options: options,
                path: temporary
            )) {
                if (
                    (unixCreateMode is null) &&
                    !OperatingSystem.IsWindows()
                ) {
                    KeepUnixMode(
                        destination: destination,
                        stream: stream
                    );
                }

                fill(
                    state: state,
                    stream: stream
                );
                stream.Flush(flushToDisk: true);
            }

            try {
                File.Move(
                    destFileName: destination,
                    overwrite: true,
                    sourceFileName: temporary
                );
            } catch (Exception exception) when (((exception is IOException or UnauthorizedAccessException) && File.Exists(path: destination))) {
                // Windows refuses to rename over a file another process holds mapped; ReplaceFile moves it aside
                // instead, and the reader's view keeps the old bytes.
                backup = Path.ChangeExtension(
                    extension: "replaced.tmp",
                    path: temporary
                );
                File.Replace(
                    destinationBackupFileName: backup,
                    destinationFileName: destination,
                    sourceFileName: temporary
                );
            }
        } finally {
            try {
                File.Delete(path: temporary);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // The failure that brought us here is the one worth reporting; the temporary stays behind.
            }

            if (
                (backup is not null) &&
                File.Exists(path: backup)
            ) {
                ReleaseBackup(
                    backup: backup,
                    destination: destination
                );
            }
        }
    }

    /// <summary>Reads every byte of a file that another thread or process may be renaming into place at the same
    /// moment. A rename holds the file open with delete access until it completes, including after the name has
    /// moved, and Windows refuses an open that does not share delete access while it does, so
    /// <see cref="File.ReadAllBytes(string)"/> can fail on a file that is already fully published. This read shares
    /// read and delete access and so does not.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static byte[] ReadAllBytes(string path) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        using var stream = new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: path,
            share: FileShare.Read | FileShare.Delete
        );
        var bytes = new byte[stream.Length];

        stream.ReadExactly(buffer: bytes);

        return bytes;
    }
    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="bytes"/>.</summary>
    /// <param name="path">The destination file; an existing file there is replaced, and a missing parent directory is
    /// created.</param>
    /// <param name="bytes">The complete new content.</param>
    /// <param name="unixCreateMode">On Unix, the mode the replacement is created with (subject to the process umask);
    /// <see langword="null"/> keeps an existing destination's mode. Ignored on Windows.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The temporary file could not be created, written, or moved over the
    /// destination. The destination is unchanged and the temporary file is deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the temporary file or replace the
    /// destination. The destination is unchanged.</exception>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes, UnixFileMode? unixCreateMode = null) =>
        Replace(
            fill: static (stream, content) => stream.Write(buffer: content),
            path: path,
            state: bytes,
            unixCreateMode: unixCreateMode
        );
    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/> as UTF-8 without a byte
    /// order mark, the encoding <see cref="File.WriteAllText(string, string)"/> writes.</summary>
    /// <param name="path">The destination file; an existing file there is replaced, and a missing parent directory is
    /// created.</param>
    /// <param name="contents">The complete new text.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="IOException">The temporary file could not be created, written, or moved over the
    /// destination. The destination is unchanged and the temporary file is deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the temporary file or replace the
    /// destination. The destination is unchanged.</exception>
    public static void WriteAllText(string path, string contents) {
        ArgumentNullException.ThrowIfNull(argument: contents);

        WriteAllBytes(
            bytes: Encoding.UTF8.GetBytes(s: contents),
            path: path
        );
    }
    /// <summary>Atomically replaces <paramref name="path"/> with whatever <paramref name="write"/> writes to the
    /// stream it is handed — for content produced by a streaming writer rather than held as one buffer.</summary>
    /// <param name="path">The destination file; an existing file there is replaced, and a missing parent directory is
    /// created.</param>
    /// <param name="write">Writes the complete new content. The stream is the temporary file; the destination is
    /// replaced only after <paramref name="write"/> returns, and an exception it throws leaves the destination
    /// unchanged and propagates.</param>
    /// <param name="unixCreateMode">On Unix, the mode the replacement is created with (subject to the process umask);
    /// <see langword="null"/> keeps an existing destination's mode. Ignored on Windows.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The temporary file could not be created, written, or moved over the
    /// destination. The destination is unchanged and the temporary file is deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not create the temporary file or replace the
    /// destination. The destination is unchanged.</exception>
    public static void Write(string path, Action<Stream> write, UnixFileMode? unixCreateMode = null) {
        ArgumentNullException.ThrowIfNull(argument: write);

        Replace(
            fill: static (stream, write) => write(obj: stream),
            path: path,
            state: write,
            unixCreateMode: unixCreateMode
        );
    }
}
