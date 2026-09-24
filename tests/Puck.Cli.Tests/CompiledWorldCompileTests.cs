using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck compile</c> writes each world document's compiled world beside it. A source and
/// the document it compiles to derive the same compiled world, a <c>.world.json</c> path compiles to its compiled world
/// alone, and a module fragment, which does not draw as a world on its own, has none and is not a failure.</summary>
public sealed class CompiledWorldCompileTests {
    private static int Compile(params string[] arguments) => ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["compile", .. arguments])).ExitCode;

    [Fact]
    public void ASourceAndItsDocumentCompileToOneCompiledWorld() {
        using var directory = new TemporaryDirectory();
        var source = directory.PathOf(name: "hgb-compare.puck");

        File.Copy(
            destFileName: source,
            sourceFileName: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/tools/hgb-compare.puck")
        );

        Assert.Equal(expected: 0, actual: Compile(source));

        var compiled = directory.PathOf(name: "hgb-compare.puckb");
        var fromSource = File.ReadAllBytes(path: compiled);

        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: fromSource, header: out var header, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: WorldDefinitionLoader.BootInstanceName, actual: header.InstanceIdentity);
        Assert.Equal(expected: CompiledWorld.EngineBuild, actual: header.EngineBuild);
        Assert.Equal(expected: ["DEFN", "ASST", "BAKE"], actual: container.Chunks.Select(selector: static chunk => chunk.Code.ToString()));
        Assert.True(condition: container.TryFind(chunk: out var bakes, code: WorldBakeChunk.BakeCode));
        Assert.True(condition: WorldBakeChunk.TryRead(keys: out var keys, packReference: out var reference, payload: bakes.Payload.Span, reason: out reason), userMessage: reason);
        Assert.Equal(actual: reference, expected: WorldBakePack.FileName);

        // The bakes the compiled world names ship in the pack beside it, which a compile without a tree writes at once.
        var packPath = directory.PathOf(name: WorldBakePack.FileName);

        if (keys.Count == 0) {
            Assert.False(condition: File.Exists(path: packPath));
        } else {
            Assert.True(condition: WorldBakePack.TryDecode(content: File.ReadAllBytes(path: packPath), pack: out var pack, reason: out reason), userMessage: reason);
            Assert.All(collection: keys, action: key => Assert.True(condition: pack.TryGet(key: key, outcome: out _)));
        }

        File.Delete(path: compiled);
        Assert.Equal(expected: 0, actual: Compile(directory.PathOf(name: "hgb-compare.world.json")));
        Assert.Equal(expected: fromSource, actual: File.ReadAllBytes(path: compiled));

        var elsewhere = directory.PathOf(name: "out/named.puckb");

        Assert.Equal(expected: 0, actual: Compile(directory.PathOf(name: "hgb-compare.world.json"), "--output", elsewhere));
        Assert.Equal(expected: fromSource, actual: File.ReadAllBytes(path: elsewhere));
    }
    [Fact]
    public void AFragmentHasNoCompiledWorldAndCompiles() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteBytes(bytes: "state { ints [] }"u8, name: "fragment.puck");

        Assert.Equal(expected: 0, actual: Compile(source));
        Assert.True(condition: File.Exists(path: directory.PathOf(name: "fragment.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "fragment.puckb")));
    }
}
