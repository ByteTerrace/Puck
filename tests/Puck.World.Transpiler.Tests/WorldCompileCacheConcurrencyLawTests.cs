using System.Text;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldCompileCache"/> stays exact when callers on several threads, and
/// instances standing in for several processes, share one source and one persisted directory. Concurrent misses on one
/// source compile it once; an edit racing readers never leaves a stale compile served once it settles; a persisted
/// entry is only ever read whole; and the directory stays bounded, abandoned temporary files included. Each law owns
/// its directory-backed cache and counts through its own attributed <see cref="WorldBootWork"/> ledger, so the
/// process-wide <see cref="WorldCompileCache.Shared"/> is never touched.</summary>
public sealed class WorldCompileCacheConcurrencyLawTests {
    private const string Module = "let initial = {0}\nmodule counter() {{ state {{ world {{ slot score = initial }} }} }}";
    private const string Root = "import \"counter.puck\"\nuse counter as root()";
    private const int Threads = 8;

    private static string ModuleAt(int version) => string.Format(
        arg0: version,
        format: Module,
        provider: System.Globalization.CultureInfo.InvariantCulture
    );
    // The compiler's own bytes for the root with the module at one version, compiled serially outside every cache;
    // the module is left at that version.
    private static byte[] Serial(TemporaryDirectory files, int version) {
        files.WriteText(name: "counter.puck", text: ModuleAt(version: version));

        return Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: files.PathOf(name: "root.puck"), cancellationToken: TestContext.Current.CancellationToken).RequireJson().ToJsonString());
    }
    private static (long Compiles, long Hits) Counts(WorldBootWork work) =>
        (work.Read(kind: WorldBootWork.Compiles), work.Read(kind: WorldBootWork.PuckCacheHits));
    // Runs body on count dedicated threads, so a barrier inside it can always be met, and rethrows the first failure.
    private static void OnThreads(int count, Action<int> body) {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(count: count, start: 0).Select(selector: index => new Thread(start: () => {
            try {
                body(obj: index);
            } catch (Exception exception) {
                failures.Enqueue(item: exception);
            }
        })).ToArray();

        foreach (var thread in threads) {
            thread.Start();
        }
        foreach (var thread in threads) {
            thread.Join();
        }

        if (failures.TryDequeue(result: out var first)) {
            throw new AggregateException(innerExceptions: [first, .. failures]);
        }
    }
    // Replaces a file whole, so a reader sees its old bytes or its new ones and never a truncated file. A replace that
    // a reader's open handle refuses is tried again.
    private static void Replace(string path, string text) {
        var temporary = (path + ".next");

        File.WriteAllText(contents: text, path: temporary);

        while (true) {
            try {
                File.Move(destFileName: path, overwrite: true, sourceFileName: temporary);

                return;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Thread.Yield();
            }
        }
    }
    // A source or module read that races a replace may fail the way any read of a file being replaced fails; nothing
    // may fail because of the cache's own directory.
    private static bool IsSourceRace(Exception exception, string cache) => (
        (exception is IOException or UnauthorizedAccessException) &&
        !exception.Message.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: cache)
    );

    [Fact]
    public void AColdConcurrentMissCompilesOnceAndAgreesWithASerialCompile() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: ModuleAt(version: 7));

        var root = files.WriteText(name: "root.puck", text: Root);
        var directory = files.PathOf(name: "cache");
        var cache = new WorldCompileCache(directory: directory);
        var expected = Serial(files: files, version: 7);
        var work = new WorldBootWork();
        var documents = new byte[Threads][];
        using var barrier = new Barrier(participantCount: Threads);

        OnThreads(count: Threads, body: index => {
            using var attribution = WorldBootWork.Attribute(work: work);

            barrier.SignalAndWait(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(condition: cache.TryCompile(compiled: out var compiled, failure: out var failure, path: root), userMessage: failure?.Diagnostics.FormatReport(filePath: root));
            documents[index] = compiled!.Document!;
        });

        Assert.Equal(expected: (1L, ((long)(Threads - 1))), actual: Counts(work: work));
        Assert.All(collection: documents, action: document => Assert.Equal(actual: document, expected: expected));
        _ = Assert.Single(collection: Directory.GetFiles(path: directory, searchPattern: "*.compiled"));
        Assert.Empty(collection: Directory.GetFiles(path: directory, searchPattern: "*.tmp"));

        var fresh = new WorldBootWork();

        using (WorldBootWork.Attribute(work: fresh)) {
            Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out var reread, failure: out _, path: root));
            Assert.Equal(expected: expected, actual: reread!.Document);
        }

        Assert.Equal(expected: (0L, 1L), actual: Counts(work: fresh));
    }
    [Fact]
    public void AnEditRacingReadersNeverLeavesAStaleCompileServed() {
        using var files = new TemporaryDirectory();

        var module = files.WriteText(name: "counter.puck", text: ModuleAt(version: 1));
        var root = files.WriteText(name: "root.puck", text: Root);
        var directory = files.PathOf(name: "cache");
        var cache = new WorldCompileCache(directory: directory);
        var versions = new[] { Serial(files: files, version: 1), Serial(files: files, version: 2) };
        var writing = 1;
        var served = 0;

        OnThreads(count: Threads, body: index => {
            if (index == 0) {
                // The edits go on until the readers have been served often enough to have raced them.
                for (var edit = 0; ((edit < 40) || (Volatile.Read(location: ref served) < 40)); ++edit) {
                    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                    Replace(path: module, text: ModuleAt(version: ((edit % 2) + 1)));
                }

                Replace(path: module, text: ModuleAt(version: 2));
                Volatile.Write(location: ref writing, value: 0);

                return;
            }

            while (Volatile.Read(location: ref writing) == 1) {
                try {
                    if (cache.TryCompile(compiled: out var compiled, failure: out _, path: root)) {
                        Assert.Contains(collection: versions, filter: version => version.AsSpan().SequenceEqual(other: compiled!.Document));
                        _ = Interlocked.Increment(location: ref served);
                    }
                } catch (Exception exception) when (IsSourceRace(cache: directory, exception: exception)) {
                    continue;
                }
            }
        });

        Assert.True(condition: (served > 0));
        Assert.True(condition: cache.TryCompile(compiled: out var settled, failure: out _, path: root));
        Assert.Equal(expected: versions[1], actual: settled!.Document);
        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out var reread, failure: out _, path: root));
        Assert.Equal(expected: versions[1], actual: reread!.Document);
    }
    [Fact]
    public void AReaderAgainstAWriterOnOnePersistedEntryOnlyEverReadsAWholeEntry() {
        using var files = new TemporaryDirectory();

        var module = files.WriteText(name: "counter.puck", text: ModuleAt(version: 1));
        var root = files.WriteText(name: "root.puck", text: Root);
        var directory = files.PathOf(name: "cache");
        var versions = new[] { Serial(files: files, version: 1), Serial(files: files, version: 2) };
        var writing = 1;
        var read = 0;

        OnThreads(count: 2, body: index => {
            if (index == 0) {
                // Each edit makes the writer's held compile stand no longer, so it compiles and rewrites the one entry.
                var writer = new WorldCompileCache(directory: directory);

                // The rewrites go on until the reader has read often enough to have raced them.
                for (var edit = 0; ((edit < 40) || (Volatile.Read(location: ref read) < 20)); ++edit) {
                    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                    Replace(path: module, text: ModuleAt(version: ((edit % 2) + 1)));

                    try {
                        _ = writer.TryCompile(compiled: out _, failure: out _, path: root);
                    } catch (Exception exception) when (IsSourceRace(cache: directory, exception: exception)) {
                        continue;
                    }
                }

                Volatile.Write(location: ref writing, value: 0);

                return;
            }

            while (Volatile.Read(location: ref writing) == 1) {
                try {
                    if (new WorldCompileCache(directory: directory).TryCompile(compiled: out var compiled, failure: out _, path: root)) {
                        Assert.Contains(collection: versions, filter: version => version.AsSpan().SequenceEqual(other: compiled!.Document));
                        _ = Interlocked.Increment(location: ref read);
                    }
                } catch (Exception exception) when (IsSourceRace(cache: directory, exception: exception)) {
                    continue;
                }
            }
        });

        Assert.True(condition: (read > 0));
        Assert.Empty(collection: Directory.GetFiles(path: directory, searchPattern: "*.tmp"));
    }
    [Fact]
    public void TheDirectoryStaysBoundedAndLosesItsAbandonedTemporaries() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: ModuleAt(version: 7));

        var root = files.WriteText(name: "root.puck", text: Root);
        var directory = files.PathOf(name: "cache");
        var old = DateTime.UtcNow.AddHours(value: -1);

        Directory.CreateDirectory(path: directory);

        // A directory already full of older entries, and writes a killed process left half done.
        for (var index = 0; (index < WorldCompileCache.MaxPersistedEntries); ++index) {
            var entry = Path.Combine(path1: directory, path2: $"{index:x32}.compiled");

            File.WriteAllBytes(bytes: [0], path: entry);
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: old, path: entry);
        }
        for (var index = 0; (index < 3); ++index) {
            var orphan = Path.Combine(path1: directory, path2: $"{index:x32}.compiled.{Guid.NewGuid():N}.tmp");

            File.WriteAllBytes(bytes: [0], path: orphan);
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: old, path: orphan);
        }

        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out _, failure: out _, path: root));
        Assert.Empty(collection: Directory.GetFiles(path: directory, searchPattern: "*.tmp"));
        Assert.True(condition: (Directory.GetFiles(path: directory).Length <= WorldCompileCache.MaxPersistedEntries));

        // The entry just written is the newest, so it is the one the bound kept.
        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out _, failure: out _, path: root));
        }

        Assert.Equal(expected: (0L, 1L), actual: Counts(work: work));
    }
}
