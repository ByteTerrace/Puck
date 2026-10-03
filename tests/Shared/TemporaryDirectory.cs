using System.Collections.Concurrent;
using System.Reflection;

[assembly: Puck.Testing.TemporaryDirectoryVerdictAttribute]

namespace Puck.Testing;

/// <summary>A directory under the temporary root that one law owns: created on construction through
/// <see cref="RunDirectory.CreatePath"/>, under a name no other directory takes, shut down on dispose in a fixed order so
/// that a host's background work never races the delete, and resolved by the law's verdict.
/// <para>
/// Shutdown is explicit, not observed. <see cref="Dispose"/> disposes everything the law gave to <see cref="Own"/>, last
/// registered first, awaiting <see cref="IAsyncDisposable.DisposeAsync"/> when the owner has one and calling
/// <see cref="IDisposable.Dispose"/> otherwise, and an owner that runs background workers returns from that disposal only
/// after every worker has joined (a host awaits its services' <c>DisposeAsync</c>; a service with a <c>Completion</c>
/// exposes it through <c>DisposeAsync</c>). The shutdown is bounded by <paramref name="teardownBound"/>: an owner that
/// does not return in time, or that throws, fails the law by its type name, and nothing under the directory is deleted
/// whatever the verdict. Once the owners have returned, the directory records every file's size and last write time.
/// </para>
/// <para>
/// The verdict decides the rest (<see cref="Conclude"/>). A law that fails keeps the directory and writes its absolute
/// path to the law's output as a <see cref="RunDirectory.KeptLine"/>, so the evidence survives. A law that passes deletes
/// it, retrying while a handle closes, within the same bound; a delete that never completes fails the law naming its
/// last error and what is still present. Nothing is deleted until every file has been read through a handle no other
/// holds, so each read is the file's final state, and a file that is new or changed since the owners returned fails the
/// law by name. A write made through a handle that outlived the owners is therefore always seen; a worker that wrote and
/// let go before the owners returned is part of the record, so the check proves nothing about it. A best-effort directory
/// (<paramref name="bestEffortDelete"/>) skips the check and leaves a directory a handle still holds to a later
/// sweep.
/// </para>
/// <para>Disposal inside a running law, before its verdict exists, defers the resolution to the end of that law
/// (<see cref="TemporaryDirectoryVerdictAttribute"/>); disposal from a test class's own <c>Dispose</c>, after the verdict,
/// resolves at once; disposal outside any law — a class or collection fixture — resolves as a pass. Names are relative
/// to <see cref="RootPath"/> and may be forward-slashed; a write creates any subdirectory its name names.</para></summary>
/// <param name="prefix">The temp-directory name prefix — kept distinct per caller so a directory that survives a failed
/// or aborted run (a killed process, a debugger break) still names which law left it behind.</param>
/// <param name="teardownBound">The bound on the owners' shutdown, and separately on a passing verdict's delete; laws can
/// supply a short bound to exercise a stuck owner or a handle that never closes.</param>
/// <param name="bestEffortDelete">Whether a passing law's deletion waits a moment for handles still closing under the
/// directory and, when one stays open, leaves the directory to a later sweep rather than failing the law: for a law that
/// composes a host whose background work may still hold a file there as it is disposed, where what the law proves is not
/// the host's file handling.</param>
internal sealed class TemporaryDirectory(string prefix = "puck-test-", TimeSpan? teardownBound = null, bool bestEffortDelete = false) : IDisposable {
    // How long a delete waits between tries, in milliseconds, and how many paths a failure message names.
    private const int DeleteRetryMilliseconds = 50;
    private const int NamedPaths = 12;

    // The directories each running law disposed before its verdict existed, keyed by the law's unique id.
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<TemporaryDirectory>> Pending = new(comparer: StringComparer.Ordinal);
    private static readonly AsyncLocal<CancellationToken> TeardownCancellation = new();
    private readonly List<IDisposable> m_owned = [];
    private readonly TimeSpan m_teardownBound = (((teardownBound ?? TestLiveness.Bound) > TimeSpan.Zero)
        ? (teardownBound ?? TestLiveness.Bound)
        : throw new ArgumentOutOfRangeException(paramName: nameof(teardownBound)));

    private int m_concluded;
    private int m_disposed;
    private Dictionary<string, (long Length, long Written)>? m_settled;
    private bool m_shutDownFailed;

    private string m_teardownStep = "starting teardown";

    /// <summary>Gets the directory's absolute path.</summary>
    public string RootPath { get; } = RunDirectory.CreatePath(prefix: prefix);

    private static bool TryDelete(string path, out Exception? failure) {
        failure = null;

        try {
            Directory.Delete(
                path: path,
                recursive: true
            );
        } catch (Exception error) when ((error is (IOException or UnauthorizedAccessException))) {
            // A handle under the directory has not closed yet, or a file is open for deletion.
            failure = error;
        }

        return !Directory.Exists(path: path);
    }
    // Every file under the directory with its size and last write time, relative to the root. A handle on the file
    // reports them as they are now; where a worker holds the file exclusively no handle can be opened, and the
    // directory entry's size and time stand in, which agree with the handle's while the file is unchanged.
    private Dictionary<string, (long Length, long Written)> Snapshot() {
        var files = new Dictionary<string, (long Length, long Written)>();

        try {
            foreach (var file in new DirectoryInfo(path: RootPath).EnumerateFiles(
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                var key = Path.GetRelativePath(path: file.FullName, relativeTo: RootPath);

                try {
                    using var stream = new FileStream(
                        access: FileAccess.Read,
                        mode: FileMode.Open,
                        path: file.FullName,
                        share: FileShare.ReadWrite | FileShare.Delete
                    );

                    files[key] = (stream.Length, File.GetLastWriteTimeUtc(fileHandle: stream.SafeFileHandle).Ticks);
                } catch (Exception error) when ((error is (IOException or UnauthorizedAccessException))) {
                    files[key] = (file.Length, file.LastWriteTimeUtc.Ticks);
                }
            }
        } catch (Exception error) when ((error is (DirectoryNotFoundException or FileNotFoundException))) {
            // The directory or a file went away while it was read; there is nothing left to compare.
        }

        return files;
    }
    private string Describe(Exception? lastFailure, List<string> strays) {
        var remaining = (Directory.Exists(path: RootPath)
            ? Directory.EnumerateFileSystemEntries(
                path: RootPath,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            ).Take(count: NamedPaths).Select(selector: entry => Path.GetRelativePath(path: entry, relativeTo: RootPath)).ToArray()
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
    private void DisposeOwned(CancellationToken cancellationToken) {
        var failures = new List<Exception>();

        for (var index = (m_owned.Count - 1); (index >= 0); --index) {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(location: ref m_teardownStep, value: $"disposing {m_owned[index].GetType().FullName}");
            try {
                if (m_owned[index] is IAsyncDisposable asynchronous) {
                    asynchronous.DisposeAsync().AsTask().GetAwaiter().GetResult();
                } else {
                    m_owned[index].Dispose();
                }
            } catch (Exception error) {
                failures.Add(item: error);
            }
        }

        m_owned.Clear();
        Throw(failures: failures);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(location: ref m_teardownStep, value: $"recording {RootPath}");
        m_settled = Snapshot();
    }
    // Reads every file under the directory through a handle no other holds, so each read is the file's final state: a
    // worker that still holds a file refuses the open, and the file is read again on the next try. A file that is new or
    // changed since the owners returned is a stray. Returns the refusal of a file still held, or null when none is.
    private Exception? ReadReleased(Dictionary<string, (long Length, long Written)> settled, List<string> strays) {
        FileInfo[] files;

        try {
            files = new DirectoryInfo(path: RootPath).GetFiles(
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            );
        } catch (Exception error) when ((error is (DirectoryNotFoundException or FileNotFoundException))) {
            return null;
        }

        Exception? held = null;

        foreach (var file in files) {
            var key = Path.GetRelativePath(path: file.FullName, relativeTo: RootPath);

            try {
                using var stream = new FileStream(
                    access: FileAccess.Read,
                    mode: FileMode.Open,
                    path: file.FullName,
                    share: FileShare.None
                );

                if (!settled.TryGetValue(key: key, value: out var before) || (before != (stream.Length, File.GetLastWriteTimeUtc(fileHandle: stream.SafeFileHandle).Ticks))) {
                    strays.Add(item: key);
                }
            } catch (Exception error) when ((error is (DirectoryNotFoundException or FileNotFoundException))) {
                // The file went away after it was listed; there is nothing left to read.
            } catch (Exception error) when ((error is (IOException or UnauthorizedAccessException))) {
                held = error;
            }
        }

        return held;
    }
    private void DeleteStrictly(CancellationToken cancellationToken) {
        var failures = new List<Exception>();
        var strays = new List<string>();

        Volatile.Write(location: ref m_teardownStep, value: $"deleting {RootPath}");
        if (!Directory.Exists(path: RootPath)) {
            failures.Add(item: new DirectoryNotFoundException(message: $"The directory {RootPath} was removed before its law finished."));
        } else {
            var settled = (m_settled ?? Snapshot());
            Exception? lastFailure = null;

            try {
                TestLiveness.Until(
                    reason: () => Describe(lastFailure: lastFailure, strays: strays),
                    step: () => {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Nothing is deleted until every file has been read alone: a delete that succeeded the moment
                        // a worker let go would take with it a write the worker made after the owners returned.
                        if (ReadReleased(settled: settled, strays: strays) is { } held) {
                            lastFailure = held;
                            Volatile.Write(location: ref m_teardownStep, value: Describe(lastFailure: lastFailure, strays: strays));

                            return false;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var deleted = TryDelete(failure: out lastFailure, path: RootPath);

                        if (!deleted) {
                            Volatile.Write(location: ref m_teardownStep, value: Describe(lastFailure: lastFailure, strays: strays));
                        }

                        return deleted;
                    },
                    wait: token => {
                        // A pause between tries; the delete has no completion signal to block on.
                        _ = token.WaitHandle.WaitOne(millisecondsTimeout: DeleteRetryMilliseconds);

                        return true;
                    }
                );
            } catch (Exception error) {
                failures.Add(item: error);
            }

            if (strays.Count > 0) {
                failures.Add(item: new InvalidOperationException(message: Describe(lastFailure: null, strays: strays)));
            }
        }

        Throw(failures: failures);
    }
    // Runs one teardown step on a dedicated worker, which keeps a blocking owner's Dispose off the test runner and needs
    // no pool timer to enforce the bound; the calling thread bounds the step, including nested directories' teardowns.
    private void RunBounded(Action<CancellationToken> step) {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: TeardownCancellation.Value);
        var cancellationToken = cancelled.Token;
        var teardown = Task.Factory.StartNew(
            action: () => {
                TeardownCancellation.Value = cancellationToken;
                step(obj: cancellationToken);
            },
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        try {
            if (teardown.Wait(timeout: m_teardownBound)) {
                return;
            }
        } catch (AggregateException) {
            teardown.GetAwaiter().GetResult();
        }

        cancelled.Cancel();
        // A step cannot be forcibly stopped. If it eventually returns, cancellation prevents disposal of its
        // dependencies or deletion underneath it; observe the worker's eventual exception without waiting for it.
        _ = teardown.ContinueWith(
            continuationAction: static task => _ = task.Exception,
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default
        );
        throw new TimeoutException(message: $"Directory teardown exceeded {m_teardownBound} while {Volatile.Read(location: ref m_teardownStep)}. Still present under {RootPath}; teardown does not delete while an owner is blocked.");
    }
    private static void Throw(List<Exception> failures) {
        if (failures.Count == 1) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failures[0]).Throw();
        } else if (failures.Count > 1) {
            throw new AggregateException(innerExceptions: failures);
        }
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
    /// <summary>Resolves the directory by a verdict, once; later calls, the deferred resolution at the end of the law
    /// among them, do nothing. A failing verdict, or any verdict after an owner failed or outlived the bound, keeps the
    /// directory and names it; a passing one deletes it. A law that proves the teardown itself drives its verdict here
    /// after <see cref="Dispose"/>.</summary>
    /// <param name="passed">Whether the law passed.</param>
    /// <exception cref="TimeoutException">A passing verdict's delete did not complete within the bound.</exception>
    /// <exception cref="InvalidOperationException">A file was written after the owners returned.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory was removed before its law finished.</exception>
    public void Conclude(bool passed) {
        if (Interlocked.Exchange(
            location1: ref m_concluded,
            value: 1
        ) != 0) {
            return;
        }
        if (!passed || m_shutDownFailed) {
            if (Directory.Exists(path: RootPath)) {
                Xunit.TestContext.Current.TestOutputHelper?.WriteLine(message: RunDirectory.KeptLine(path: RootPath));
            }

            return;
        }
        if (bestEffortDelete) {
            RunDirectory.TryDelete(path: RootPath);

            return;
        }

        RunBounded(step: DeleteStrictly);
    }
    /// <summary>Shuts the owners down, within the bound, then resolves the directory by the law's verdict: at once when
    /// the verdict exists or no law is running, otherwise at the end of the running law.</summary>
    /// <exception cref="TimeoutException">An owner did not return within the bound.</exception>
    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }

        try {
            RunBounded(step: DisposeOwned);
        } catch {
            m_shutDownFailed = true;

            throw;
        } finally {
            var context = Xunit.TestContext.Current;

            if (
                (context.Test is { } test) &&
                (context.TestStatus == Xunit.TestEngineStatus.Running) &&
                (context.TestState is null)
            ) {
                Pending.GetOrAdd(
                    key: test.UniqueID,
                    valueFactory: static _ => new()
                ).Enqueue(item: this);
            } else if (m_shutDownFailed) {
                Conclude(passed: false);
            } else {
                Conclude(passed: (context.TestState?.Result != Xunit.TestResult.Failed));
            }
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

    /// <summary>Resolves every directory the law <paramref name="testId"/> disposed before its verdict, each in the
    /// order it was disposed; every directory is resolved before any failure is thrown.</summary>
    /// <param name="testId">The law's unique id.</param>
    /// <param name="passed">Whether the law passed.</param>
    internal static void ResolvePending(string testId, bool passed) {
        if (!Pending.TryRemove(
            key: testId,
            value: out var directories
        )) {
            return;
        }

        var failures = new List<Exception>();

        foreach (var directory in directories) {
            try {
                directory.Conclude(passed: passed);
            } catch (Exception exception) {
                failures.Add(item: exception);
            }
        }

        Throw(failures: failures);
    }
}
/// <summary>Resolves, once each law has run and its verdict exists, the <see cref="TemporaryDirectory"/> instances it
/// disposed while it ran. Applied to every assembly that compiles <see cref="TemporaryDirectory"/>.</summary>
[AttributeUsage(
    validOn: AttributeTargets.Assembly,
    AllowMultiple = false
)]
internal sealed class TemporaryDirectoryVerdictAttribute : Xunit.v3.BeforeAfterTestAttribute {
    public override void After(MethodInfo methodUnderTest, Xunit.v3.IXunitTest test) => TemporaryDirectory.ResolvePending(
        passed: (Xunit.TestContext.Current.TestState?.Result != Xunit.TestResult.Failed),
        testId: test.UniqueID
    );
}
