using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Modules;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldCompileCache"/> compiles an unchanged source once — in one process
/// and, through its directory, across processes — and hands back exactly the bytes the compiler produces; it
/// recompiles exactly the sources whose recorded file facts moved (an imported module's bytes, a path a probe found
/// absent), never serves a torn or foreign persisted entry, keeps the entries of builds sharing a directory apart,
/// records every file fact as a full path, and never holds a compile that failed. Each law counts
/// through its own attributed <see cref="WorldBootWork"/> ledger.</summary>
public sealed class WorldCompileCacheLawTests {
    private const string Counter = "let initial = 7\nmodule counter() { state { world { slot score = initial } } }";

    private static (long Compiles, long Hits) Compile(WorldCompileCache cache, params string[] paths) {
        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            foreach (var path in paths) {
                Assert.True(
                    condition: cache.TryCompile(
                        compiled: out _,
                        failure: out var failure,
                        path: path
                    ),
                    userMessage: failure?.Diagnostics.FormatReport(filePath: path)
                );
            }
        }

        return (work.Read(kind: WorldBootWork.Compiles), work.Read(kind: WorldBootWork.PuckCacheHits));
    }
    // The persisted entry format, written independently of the cache: the magic, then UTF-8 length-prefixed fields —
    // the writing build, the key, the file facts, the document, whether it emits one, no worlds, no schema, no tests —
    // and a trailing SHA-256 over all of it. The one fact recorded is the source's current bytes, so the entry stands.
    private static byte[] Forge(string build, string key, string source, byte[] document) {
        using var stream = new MemoryStream();

        stream.Write(buffer: "PUCKWCC3"u8);
        using (var writer = new BinaryWriter(encoding: Encoding.UTF8, leaveOpen: true, output: stream)) {
            writer.Write(value: build);
            writer.Write(value: key);
            writer.Write(value: 1);
            writer.Write(value: Convert.ToHexStringLower(inArray: System.Security.Cryptography.SHA256.HashData(source: File.ReadAllBytes(path: source))));
            writer.Write(value: ((byte)CompileInputKind.Content));
            writer.Write(value: Path.GetFullPath(path: source));
            writer.Write(value: true);
            writer.Write(value: document.Length);
            writer.Write(buffer: document);
            writer.Write(value: true);
            writer.Write(value: 0);
            writer.Write(value: false);
            writer.Write(value: 0);
        }

        stream.Write(buffer: System.Security.Cryptography.SHA256.HashData(source: stream.ToArray()));

        return stream.ToArray();
    }
    // The build that wrote an entry, the first field after the magic.
    private static string BuildOf(string entry) {
        using var reader = new BinaryReader(encoding: Encoding.UTF8, input: new MemoryStream(buffer: File.ReadAllBytes(path: entry)));

        _ = reader.ReadBytes(count: 8);

        return reader.ReadString();
    }
    // An entry's file name: the first 32 hex digits of the SHA-256 of the writing build and the key.
    private static string EntryName(string build, string key) =>
        (Convert.ToHexStringLower(inArray: System.Security.Cryptography.SHA256.HashData(source: Encoding.UTF8.GetBytes(s: $"{build}\n{key}")))[..32] + ".compiled");
    // A source's key: its full path spelled with '/' as its only separator, upper-cased where paths ignore case.
    private static string KeyOf(string path) {
        var full = Path.GetFullPath(path: path).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);

        return (OperatingSystem.IsWindows()
            ? full.ToUpperInvariant()
            : full
        );
    }
    // The key an entry records, the second field after the magic.
    private static string KeyIn(string entry) {
        using var reader = new BinaryReader(encoding: Encoding.UTF8, input: new MemoryStream(buffer: File.ReadAllBytes(path: entry)));

        _ = reader.ReadBytes(count: 8);
        _ = reader.ReadString();

        return reader.ReadString();
    }
    private static string User(TemporaryDirectory files, string name, string module = "counter") =>
        files.WriteText(
            name: $"{name}.puck",
            text: $"import \"{module}.puck\"\nuse {module} as {name}()"
        );

    [Fact]
    public void AnUnchangedSourceCompilesOnceAndReadsBackTheCompilersOwnBytes() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: Counter);

        var root = User(files: files, name: "root");
        var cache = new WorldCompileCache();

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: cache, paths: root));
        Assert.Equal(expected: (0L, 1L), actual: Compile(cache: cache, paths: root));
        Assert.True(condition: cache.TryCompile(compiled: out var compiled, failure: out _, path: root));
        Assert.Equal(
            expected: Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: root, cancellationToken: TestContext.Current.CancellationToken).RequireJson().ToJsonString()),
            actual: compiled!.Document
        );
        // The held compile rests on the source and the module its import walk read, byte for byte.
        Assert.Equal(
            expected: [
                (Path.GetFileName(path: root), CompileInputKind.Content),
                ("counter.puck", CompileInputKind.Content),
            ],
            actual: compiled.Inputs.Where(predicate: static input => (input.Kind == CompileInputKind.Content)).Select(selector: static input => (Path.GetFileName(path: input.Path), input.Kind))
        );
    }
    [Fact]
    public void ASecondProcessOverTheSameDirectoryCompilesNothing() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: Counter);

        var root = User(files: files, name: "root");
        var directory = files.PathOf(name: "cache");

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));

        var second = new WorldCompileCache(directory: directory);

        Assert.Equal(expected: (0L, 1L), actual: Compile(cache: second, paths: root));
        Assert.True(condition: second.TryCompile(compiled: out var compiled, failure: out _, path: root));
        Assert.Equal(
            expected: Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: root, cancellationToken: TestContext.Current.CancellationToken).RequireJson().ToJsonString()),
            actual: compiled!.Document
        );
    }
    [Fact]
    public void AnEntryAnotherBuildWroteIsNeitherServedNorOverwritten() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: Counter);

        var root = User(files: files, name: "root");
        var directory = files.PathOf(name: "cache");
        var key = KeyOf(path: root);
        var forged = "{\"forged\":true}"u8.ToArray();

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));

        var ours = Assert.Single(collection: Directory.GetFiles(path: directory, searchPattern: "*.compiled"));
        var build = BuildOf(entry: ours);

        // This build's entry is named for this build and this source.
        Assert.Equal(expected: EntryName(build: build, key: key), actual: Path.GetFileName(path: ours));

        // The forger writes exactly what a build reads: under this build's own name and header, its entry is served.
        File.WriteAllBytes(bytes: Forge(build: build, document: forged, key: key, source: root), path: ours);
        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out var served, failure: out _, path: root));
        Assert.Equal(expected: forged, actual: served!.Document);
        File.Delete(path: ours);

        // Another build's entry, under the name and header that build writes, is neither served nor overwritten.
        var foreignPath = Path.Combine(path1: directory, path2: EntryName(build: "another build", key: key));
        var foreign = Forge(build: "another build", document: forged, key: key, source: root);

        File.WriteAllBytes(bytes: foreign, path: foreignPath);

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));
        Assert.Equal(expected: (0L, 1L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));
        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out var compiled, failure: out _, path: root));
        Assert.Equal(
            expected: Encoding.UTF8.GetBytes(s: WorldCompiler.CompileFile(path: root, cancellationToken: TestContext.Current.CancellationToken).RequireJson().ToJsonString()),
            actual: compiled!.Document
        );
        Assert.Equal(expected: foreign, actual: File.ReadAllBytes(path: foreignPath));
        Assert.Equal(expected: 2, actual: Directory.GetFiles(path: directory, searchPattern: "*.compiled").Length);
    }
    [Fact]
    public void EverySpellingOfOneSourceSharesOneForwardSlashKey() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: Counter);

        var root = User(files: files, name: "root");
        var directory = files.PathOf(name: "cache");
        var forward = root.Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);
        var cache = new WorldCompileCache(directory: directory);

        Assert.DoesNotContain(actualString: forward, expectedSubstring: "\\");
        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: cache, paths: root));

        if (OperatingSystem.IsWindows()) {
            // A backslash is a separator here, so the backslash and forward-slash spellings name one file and one key.
            Assert.Contains(actualString: root, expectedSubstring: "\\");
            Assert.Equal(expected: (0L, 1L), actual: Compile(cache: cache, paths: forward));
            Assert.Equal(expected: (0L, 1L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: forward));
        }

        var entry = Assert.Single(collection: Directory.GetFiles(path: directory, searchPattern: "*.compiled"));
        var key = KeyIn(entry: entry);

        Assert.DoesNotContain(actualString: key, expectedSubstring: "\\");
        Assert.Equal(expected: KeyOf(path: forward), actual: key);
        Assert.Equal(expected: KeyOf(path: root), actual: key);
    }
    [Fact]
    public void ACompileOfARelativePathRecordsEveryFactAsAFullPath() {
        // The fixture sits under the current directory, so the source has a relative spelling on any volume layout.
        var name = $"puck-compile-cache-{Guid.NewGuid():N}";
        var directory = Directory.CreateDirectory(path: Path.Combine(path1: Environment.CurrentDirectory, path2: name));

        try {
            File.WriteAllText(contents: Counter, path: Path.Combine(path1: directory.FullName, path2: "counter.puck"));
            File.WriteAllText(contents: "import \"counter.puck\"\nuse counter as root()", path: Path.Combine(path1: directory.FullName, path2: "root.puck"));

            var relative = Path.Combine(path1: name, path2: "root.puck");

            Assert.False(condition: Path.IsPathFullyQualified(path: relative));
            Assert.True(condition: new WorldCompileCache().TryCompile(compiled: out var compiled, failure: out var failure, path: relative), userMessage: failure?.Diagnostics.FormatReport(filePath: relative));
            Assert.NotEmpty(collection: compiled!.Inputs);
            Assert.All(collection: compiled.Inputs, action: static input => Assert.True(condition: Path.IsPathFullyQualified(path: input.Path), userMessage: input.Path));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void EditingAnImportedModuleRecompilesExactlyItsDependents() {
        using var files = new TemporaryDirectory();

        var counter = files.WriteText(name: "counter.puck", text: Counter);

        files.WriteText(name: "tally.puck", text: "module tally() { state { world { slot count = 1 } } }");

        var first = User(files: files, name: "first");
        var second = User(files: files, name: "second");
        var bystander = User(files: files, module: "tally", name: "bystander");
        var cache = new WorldCompileCache(directory: files.PathOf(name: "cache"));

        Assert.Equal(expected: (3L, 0L), actual: Compile(cache: cache, paths: [first, second, bystander]));

        File.WriteAllText(contents: Counter.Replace(comparisonType: StringComparison.Ordinal, newValue: "initial = 8", oldValue: "initial = 7"), path: counter);

        Assert.Equal(expected: (2L, 1L), actual: Compile(cache: cache, paths: [first, second, bystander]));
        Assert.True(condition: cache.TryCompile(compiled: out var edited, failure: out _, path: first));
        Assert.Equal(
            expected: "8",
            actual: JsonNode.Parse(utf8Json: edited!.Document!)!["state"]!["world"]![0]!["value"]!.ToString()
        );
        // A second process sees the same edit through the persisted entries.
        Assert.Equal(expected: (0L, 3L), actual: Compile(cache: new WorldCompileCache(directory: files.PathOf(name: "cache")), paths: [first, second, bystander]));
    }
    [Fact]
    public void AFileAppearingWhereAProbeFoundNoneRecompilesTheSource() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "neighbour.world.json", text: "{}");

        var root = files.WriteText(name: "root.puck", text: "schema: \"puck.world.definition.v1\"\nimport \"neighbour\"\n");
        var cache = new WorldCompileCache();

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: cache, paths: root));
        Assert.Equal(expected: (0L, 1L), actual: Compile(cache: cache, paths: root));

        // The source beside the document now wins the name, so what the compile probed has moved.
        files.WriteText(name: "neighbour.puck", text: "schema: \"puck.world.definition.v1\"\n");

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: cache, paths: root));
    }
    [Fact]
    public void ATornPersistedEntryIsAMissAndIsReplaced() {
        using var files = new TemporaryDirectory();

        files.WriteText(name: "counter.puck", text: Counter);

        var root = User(files: files, name: "root");
        var directory = files.PathOf(name: "cache");

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));

        var entry = Assert.Single(collection: Directory.GetFiles(path: directory, searchPattern: "*.compiled"));
        var bytes = File.ReadAllBytes(path: entry);

        bytes[(bytes.Length / 2)] ^= 0x5A;
        File.WriteAllBytes(bytes: bytes, path: entry);

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));
        Assert.Equal(expected: (0L, 1L), actual: Compile(cache: new WorldCompileCache(directory: directory), paths: root));
    }
    [Fact]
    public void ASourceThatDoesNotCompileIsNeverHeld() {
        using var files = new TemporaryDirectory();

        var root = files.WriteText(name: "root.puck", text: "import \"missing.puck\"\nuse missing()");
        var cache = new WorldCompileCache();
        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            Assert.False(condition: cache.TryCompile(compiled: out var none, failure: out var failure, path: root));
            Assert.Null(@object: none);
            Assert.True(condition: failure!.Diagnostics.HasErrors);
            Assert.False(condition: cache.TryCompile(compiled: out _, failure: out _, path: root));
        }

        Assert.Equal(expected: 2L, actual: work.Read(kind: WorldBootWork.Compiles));
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldBootWork.PuckCacheHits));

        files.WriteText(name: "missing.puck", text: Counter.Replace(comparisonType: StringComparison.Ordinal, newValue: "missing", oldValue: "counter"));

        Assert.Equal(expected: (1L, 0L), actual: Compile(cache: cache, paths: root));
    }
}
