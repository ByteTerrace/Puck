using System.Text;
using Puck.Testing;
using Puck.Tests.Shared;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A product of the compiler or of the engine's derivations is keyed by the code as it runs: a hot-reload update to that
// code keys what a process compiles and derives from then on apart from the build's own, held in memory or persisted,
// and an update to code the product does not read leaves its key, and every persisted entry under it, as they were.
[Collection(name: nameof(AppliedCodeUpdateCollection))]
public sealed class WorldAppliedCodeUpdateLawTests {
    private static long Compiles(Action action) {
        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            action();
        }

        return work.Read(kind: WorldBootWork.Compiles);
    }

    [Fact]
    public void AnUpdateToTheCompilerCompilesAgainUnderItsOwnKey() {
        using var files = new TemporaryDirectory();
        _ = files.WriteText(name: "counter.puck", text: "module counter() { state { world { slot score = 3 } } }");

        var root = files.WriteText(name: "root.puck", text: "import \"counter\"\nuse counter as root()");
        var directory = files.PathOf(name: "cache");
        var cache = new WorldCompileCache(directory: directory);
        byte[]? document = null;
        void Compile() {
            Assert.True(condition: cache.TryCompile(compiled: out var compiled, failure: out var failure, path: root), userMessage: failure?.Diagnostics.FormatReport(filePath: root));
            document = compiled!.Document;
        }

        Assert.Equal(expected: 1L, actual: Compiles(action: Compile));
        Assert.Equal(expected: 0L, actual: Compiles(action: Compile));

        // An update elsewhere serves the held compile.
        AppliedCodeUpdate.Apply(typeof(WorldAppliedCodeUpdateLawTests));
        Assert.Equal(expected: 0L, actual: Compiles(action: Compile));
        _ = Assert.Single(collection: Directory.GetFiles(path: directory, searchPattern: "*.compiled"));

        // An update to the compiler serves neither the compile held in memory nor the one persisted, and persists its own
        // beside the build's.
        var built = document;

        AppliedCodeUpdate.Apply(typeof(WorldCompiler));
        Assert.Equal(expected: 1L, actual: Compiles(action: Compile));
        Assert.Equal(expected: built, actual: document);
        Assert.Equal(expected: 2, actual: Directory.GetFiles(path: directory, searchPattern: "*.compiled").Length);
        Assert.Equal(expected: 0L, actual: Compiles(action: () => Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(compiled: out _, failure: out _, path: root))));
    }
    [Fact]
    public void AnUpdateToTheParserPersistsItsDeclarationsUnderItsOwnKey() {
        using var files = new TemporaryDirectory();
        var compilations = files.PathOf(name: "compilations");
        // Bytes no other law reads, so no declaration of them is held already.
        var content = Encoding.UTF8.GetBytes(s: $"// {Guid.NewGuid():N}\nmodule lives() {{ state {{ world {{ slot lives = 2 }} }} }}");
        string[] Declarations() => Directory.GetFiles(path: Path.Combine(path1: compilations, path2: "declarations"));

        Assert.NotNull(@object: WorldSourceIndex.Declaration(compilations: compilations, content: content));
        _ = Assert.Single(collection: Declarations());

        AppliedCodeUpdate.Apply(typeof(WorldCompiler));
        Assert.NotNull(@object: WorldSourceIndex.Declaration(compilations: compilations, content: content));
        Assert.Equal(expected: 2, actual: Declarations().Length);
    }
    [Fact]
    public void AnUpdateToTheEngineMovesTheCompiledWorldsBuildAndOneElsewhereDoesNot() {
        var before = CompiledWorld.EngineBuild;

        AppliedCodeUpdate.Apply(typeof(WorldAppliedCodeUpdateLawTests));
        Assert.Equal(expected: before, actual: CompiledWorld.EngineBuild);

        // The container is in the producer's assembly but does not derive a chunk.
        AppliedCodeUpdate.Apply(typeof(CompiledWorld));
        Assert.Equal(expected: before, actual: CompiledWorld.EngineBuild);

        AppliedCodeUpdate.Apply(typeof(WorldDefinition));

        var applied = CompiledWorld.EngineBuild;

        Assert.NotEqual(expected: before, actual: applied);
        Assert.Equal(expected: applied, actual: CompiledWorld.EngineBuild);
    }
}
