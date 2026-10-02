using System.Collections.Concurrent;
using System.Reflection;

[assembly: Puck.Testing.TemporaryDirectoryVerdictAttribute]

namespace Puck.Testing;

/// <summary>A directory under the temporary root that one law owns: created on construction through
/// <see cref="RunDirectory.CreatePath"/>, under a name no other directory takes, and resolved by the law's verdict. A
/// law that passes deletes it whole; a law that fails keeps it and writes its absolute path to the law's output as a
/// <see cref="RunDirectory.KeptLine"/>, so the evidence survives. A deletion failure fails the law rather than masking a
/// handle the code under test left open, unless the law asks for a best-effort delete. Names are relative to
/// <see cref="RootPath"/> and may be forward-slashed; a write creates any subdirectory its name names.
/// <para>Disposal inside a running law, before its verdict exists, defers the resolution to the end of that law
/// (<see cref="TemporaryDirectoryVerdictAttribute"/>); disposal from a test class's own <c>Dispose</c>, after the verdict,
/// resolves at once; disposal outside any law — a class or collection fixture — deletes as a pass.</para></summary>
/// <param name="prefix">The temp-directory name prefix — kept distinct per caller so a directory that survives a failed
/// or aborted run (a killed process, a debugger break) still names which law left it behind.</param>
/// <param name="bestEffortDelete">Whether a passing law's deletion waits a moment for handles still closing under the
/// directory and, when one stays open, leaves the directory to a later sweep rather than failing the law: for a law that
/// composes a host whose background work may still hold a file there as it is disposed, where what the law proves is not
/// the host's file handling.</param>
internal sealed class TemporaryDirectory(string prefix = "puck-test-", bool bestEffortDelete = false) : IDisposable {
    // The directories each running law disposed before its verdict existed, keyed by the law's unique id.
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<TemporaryDirectory>> Pending = new(comparer: StringComparer.Ordinal);

    private int m_disposed;

    /// <summary>Gets the directory's absolute path.</summary>
    public string RootPath { get; } = RunDirectory.CreatePath(prefix: prefix);

    public void Dispose() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) {
            return;
        }

        var context = Xunit.TestContext.Current;

        if (
            (context.Test is { } test) &&
            (context.TestStatus == Xunit.TestEngineStatus.Running)
        ) {
            if (context.TestState is { } state) {
                Resolve(passed: (state.Result != Xunit.TestResult.Failed));
            } else {
                Pending.GetOrAdd(
                    key: test.UniqueID,
                    valueFactory: static _ => new()
                ).Enqueue(item: this);
            }

            return;
        }

        Resolve(passed: true);
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

    /// <summary>Resolves every directory the law <paramref name="testId"/> disposed before its verdict.</summary>
    /// <param name="testId">The law's unique id.</param>
    /// <param name="passed">Whether the law passed.</param>
    internal static void ResolvePending(string testId, bool passed) {
        if (!Pending.TryRemove(
            key: testId,
            value: out var directories
        )) {
            return;
        }

        List<Exception>? failures = null;

        foreach (var directory in directories) {
            try {
                directory.Resolve(passed: passed);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                (failures ??= []).Add(item: exception);
            }
        }

        if (failures is not null) {
            throw new AggregateException(innerExceptions: failures);
        }
    }

    private void Resolve(bool passed) {
        if (!passed) {
            if (Directory.Exists(path: RootPath)) {
                Xunit.TestContext.Current.TestOutputHelper?.WriteLine(message: RunDirectory.KeptLine(path: RootPath));
            }

            return;
        }
        if (bestEffortDelete) {
            RunDirectory.TryDelete(path: RootPath);

            return;
        }

        RunDirectory.Delete(path: RootPath);
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
