namespace Puck.Testing;

/// <summary>A directory under the temporary root that one law owns: created on construction, under a name no other
/// directory takes, and torn down on dispose in a fixed order so that a host's background work never races the delete.
/// Disposal first disposes everything the law gave to <see cref="Own"/> (last registered first, so a host goes before
/// the services it was composed over), then watches the directory for writes that still arrive after that, then deletes
/// it, retrying while a handle closes under a bound shared with every other wait on the thread pool
/// (<see cref="TestLiveness.Bound"/>). A directory that cannot be deleted, or that something wrote to after its owners
/// were disposed, fails the law naming the paths involved, so a worker a host returned from disposal without joining
/// is reported by the file it wrote instead of by an intermittent delete failure. Names are relative to
/// <see cref="RootPath"/> and may be forward-slashed; a write creates any subdirectory its name names.</summary>
/// <param name="prefix">The temp-directory name prefix — kept distinct per caller so a directory that survives an
/// aborted run (a killed process, a debugger break) still names which law left it behind.</param>
internal sealed class TemporaryDirectory(string prefix = "puck-test-") : IDisposable {
    // How long a delete waits between tries, in milliseconds, and how many paths a failure message names.
    private const int DeleteRetryMilliseconds = 50;
    private const int NamedPaths = 12;
    // The watcher's buffer, large enough that a burst of writes under the directory is not dropped.
    private const int WatcherBufferBytes = 65536;

    private readonly List<IDisposable> m_owned = [];

    /// <summary>Gets the directory's absolute path.</summary>
    public string RootPath { get; } = Directory.CreateTempSubdirectory(prefix: prefix).FullName;

    private static bool TryDelete(string path, out Exception? failure) {
        failure = null;

        try {
            Directory.Delete(
                path: path,
                recursive: true
            );
        } catch (Exception error) when (error is (IOException or UnauthorizedAccessException)) {
            // A handle under the directory has not closed yet, or a file is open for deletion.
            failure = error;
        }

        return !Directory.Exists(path: path);
    }

    private string Describe(Exception? lastFailure, List<string> strays) {
        var remaining = (Directory.Exists(path: RootPath)
            ? Directory.EnumerateFileSystemEntries(
                path: RootPath,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            ).Take(count: NamedPaths).Select(selector: entry => Path.GetRelativePath(path: RootPath, relativeTo: entry)).ToArray()
            : []
        );

        return string.Join(
            separator: Environment.NewLine,
            values: [
                $"The directory {RootPath} still has an owner after the law's hosts were disposed: {(lastFailure?.Message ?? "no delete failure was recorded")}",
                $"Still present: {((remaining.Length == 0) ? "nothing" : string.Join(separator: ", ", values: remaining))}",
                $"Written after the owned objects were disposed: {((strays.Count == 0) ? "nothing observed" : string.Join(separator: ", ", values: strays.Distinct().Take(count: NamedPaths)))}",
            ]
        );
    }
    private void DisposeOwned(List<Exception> failures) {
        for (var index = (m_owned.Count - 1); (index >= 0); --index) {
            try {
                m_owned[index].Dispose();
            } catch (Exception error) {
                failures.Add(item: error);
            }
        }

        m_owned.Clear();
    }

    /// <summary>Hands <paramref name="owner"/> to this directory, which disposes it before anything under the directory
    /// is deleted: a host the law composed over this directory is registered here, so the delete never runs while the
    /// host's background work is still live.</summary>
    /// <typeparam name="T">The owner's type.</typeparam>
    /// <param name="owner">The object whose disposal must finish before the directory is deleted.</param>
    /// <returns><paramref name="owner"/>, so the registration reads as part of its construction.</returns>
    public T Own<T>(T owner) where T : IDisposable {
        m_owned.Add(item: owner);

        return owner;
    }
    public void Dispose() {
        var failures = new List<Exception>();
        var strays = new List<string>();

        DisposeOwned(failures: failures);
        if (!Directory.Exists(path: RootPath)) {
            failures.Add(item: new DirectoryNotFoundException(message: $"The directory {RootPath} was removed before its law finished."));
        } else {
            using var watcher = new FileSystemWatcher(path: RootPath) {
                IncludeSubdirectories = true,
                InternalBufferSize = WatcherBufferBytes,
                NotifyFilter = (NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.Size),
            };

            void Stray(object sender, FileSystemEventArgs change) {
                lock (strays) {
                    strays.Add(item: Path.GetRelativePath(path: RootPath, relativeTo: change.FullPath));
                }
            }

            watcher.Changed += Stray;
            watcher.Created += Stray;
            watcher.Renamed += Stray;
            watcher.EnableRaisingEvents = true;

            Exception? lastFailure = null;

            try {
                TestLiveness.Until(
                    reason: () => Describe(lastFailure: lastFailure, strays: strays),
                    step: () => TryDelete(failure: out lastFailure, path: RootPath),
                    wait: token => {
                        // A pause between tries; the delete has no completion signal to block on.
                        _ = token.WaitHandle.WaitOne(millisecondsTimeout: DeleteRetryMilliseconds);

                        return true;
                    }
                );
            } catch (Exception error) {
                failures.Add(item: error);
            }

            watcher.EnableRaisingEvents = false;
            lock (strays) {
                if (strays.Count > 0) {
                    failures.Add(item: new InvalidOperationException(message: Describe(lastFailure: null, strays: strays)));
                }
            }
        }

        if (failures.Count == 1) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failures[0]).Throw();
        } else if (failures.Count > 1) {
            throw new AggregateException(innerExceptions: failures);
        }
    }
    /// <summary>Returns the absolute path of <paramref name="name"/> under this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <returns>The absolute path; nothing is created.</returns>
    public string PathOf(string name) => Path.Combine(
        path1: RootPath,
        path2: name
    );
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="name"/> under this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <param name="bytes">The file's contents.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteBytes(string name, ReadOnlySpan<byte> bytes) {
        var path = PathOf(name: name);

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );

        return path;
    }
    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte-order mark to <paramref name="name"/> under
    /// this directory.</summary>
    /// <param name="name">The relative path.</param>
    /// <param name="text">The file's contents.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteText(string name, string text) => WriteBytes(
        bytes: System.Text.Encoding.UTF8.GetBytes(s: text),
        name: name
    );
}
