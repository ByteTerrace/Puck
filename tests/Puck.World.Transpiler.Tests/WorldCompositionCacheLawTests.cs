using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class WorldCompositionCacheLawTests {
    private static (string Json, string Hash, long Compositions) Compose(string root, string cache, string catalog = "") {
        WorldDefinitionFileSource.ForgetComposedDocuments();
        var source = new PuckDocumentComposer(cache: new WorldCompileCache(directory: cache));
        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);

        Assert.True(condition: WorldDefinitionFileSource.TryComposeChainWithImports(
            source, root, File.ReadAllBytes(path: root), out var composed, out var chain, out var reason, catalogFingerprint: catalog), userMessage: reason);
        return (composed!.ToJsonString(), WorldDefinitionFileSource.ComputeChainContentHash(chain: chain), work.Read(kind: WorldBootWork.Compositions));
    }

    [InlineData("root.world.json")]
    [InlineData("basis.world.json")]
    [InlineData("module.puck")]
    [Theory]
    public void APersistedCompositionMissesEveryChangedInput(string changed) {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "root.world.json", text: "{\"basis\":\"basis\",\"imports\":[{\"document\":\"module\"}],\"documentId\":\"first\"}");

        files.WriteText(name: "basis.world.json", text: "{\"simulation\":{\"rateHz\":120}}");
        files.WriteText(name: "module.puck", text: "state { world { slot score = 1 } }");
        var directory = files.PathOf(name: "cache");
        var first = Compose(root, directory);

        Assert.True(condition: (first.Compositions > 0));
        var warm = Compose(root, directory);

        Assert.Equal(actual: warm.Compositions, expected: 0);
        Assert.Equal(actual: warm.Json, expected: first.Json);
        Assert.Equal(actual: warm.Hash, expected: first.Hash);
        var path = files.PathOf(name: changed);
        var text = File.ReadAllText(path: path);

        File.WriteAllText(path, changed switch {
            "root.world.json" => text.Replace(newValue: "second", oldValue: "first"),
            "basis.world.json" => text.Replace(newValue: "240", oldValue: "120"),
            _ => text.Replace(newValue: "= 2", oldValue: "= 1"),
        });
        var edited = Compose(root, directory);

        Assert.True(condition: (edited.Compositions > 0));
        Assert.NotEqual(actual: edited.Json, expected: first.Json);
        Assert.NotEqual(actual: edited.Hash, expected: first.Hash);
        Assert.Equal(0, Compose(root, directory).Compositions);
        Assert.True(condition: (Compose(cache: directory, catalog: "another-catalog", root: root).Compositions > 0));
    }
    [Fact]
    public void DocumentOnlyCompilesDoNotLowerTestsAndCannotAnswerATestCompile() {
        using var files = new TemporaryDirectory();
        var path = files.WriteText(name: "root.puck", text: "state { world { slot score = 1 } }\ntest \"score\" { expect { score == 1 } }");
        var directory = files.PathOf(name: "cache");
        var cache = new WorldCompileCache(directory: directory);

        Assert.True(condition: cache.TryCompile(path, out var document, out var failure), userMessage: failure?.Diagnostics.FormatReport(path));
        Assert.Empty(collection: document!.Tests);
        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(path, out var tested, out failure, includeTests: true), userMessage: failure?.Diagnostics.FormatReport(path));
        Assert.Single(collection: tested!.Tests);
        Assert.Equal(document.Document, tested.Document);
        Assert.True(condition: new WorldCompileCache(directory: directory).TryCompile(path, out var warm, out failure), userMessage: failure?.Diagnostics.FormatReport(path));
        Assert.Empty(collection: warm!.Tests);
    }
}
