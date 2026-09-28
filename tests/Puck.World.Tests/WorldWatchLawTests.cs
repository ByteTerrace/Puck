using System.Diagnostics;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using System.Text;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Real compiler inputs drive one ordinary reload request per quiet burst, with no unrelated-file trigger.</summary>
public sealed class WorldWatchLawTests {
    private static long At(int milliseconds) => ((Stopwatch.Frequency * milliseconds) / 1000);

    private const string Module = "let initial = 7\nmodule counter() { state { world { slot score = initial } } }";

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void DocumentImportsRemainWatchedThroughAWarmCompositionCache(bool source) {
        using var files = new TemporaryDirectory();
        var child = files.WriteText(name: "child.world.json", text: "{\"state\":{\"world\":{\"score\":1}}}");
        const string Json = "{\"imports\":[{\"document\":\"child\"}]}";
        var root = files.WriteText(name: (source ? "root.puck" : "root.world.json"), text: (source ? "imports [ { document: \"child\" } ]" : Json));
        var bytes = (source ? (WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out _, path: root) ? compiled!.Document! : throw new InvalidOperationException(message: "fixture source did not compile")) : Encoding.UTF8.GetBytes(s: Json));

        Assert.True(condition: PuckDocumentComposer.TryComposeWorldDocument(root, bytes, out _, out _, out var reason), userMessage: reason);
        var count = 0;
        var watch = new WorldSourceWatch(() => root);

        watch.Start(submitReload: () => count++);
        watch.Poll(now: At(milliseconds: 1000));
        Assert.Equal(actual: count, expected: 0);
        File.WriteAllText(contents: "{\"state\":{\"world\":{\"score\":2}}}", path: child);
        watch.Poll(now: At(milliseconds: 1100));
        watch.Poll(now: At(milliseconds: 1250));
        Assert.Equal(actual: count, expected: 1);
    }
    [Fact]
    public void ImportedBytesTriggerOnceAndPendingReloadRetainsLaterChanges() {
        using var files = new TemporaryDirectory();
        using var unrelated = new TemporaryDirectory();
        var module = files.WriteText(name: "counter.puck", text: Module);
        var root = files.WriteText(name: "root.puck", text: "import \"counter.puck\"\nuse counter as root()");
        var count = 0;
        var watch = new WorldSourceWatch(() => root);

        watch.Start(submitReload: () => count++);
        unrelated.WriteText(name: "other.puck", text: Module);
        watch.Poll(now: At(milliseconds: 1000));
        Assert.Equal(actual: count, expected: 0);
        File.WriteAllText(module, Module.Replace(comparisonType: StringComparison.Ordinal, newValue: "8", oldValue: "7"));
        watch.Poll(now: At(milliseconds: 1050));
        watch.Poll(now: At(milliseconds: 1199));
        Assert.Equal(actual: count, expected: 0);
        watch.Poll(now: At(milliseconds: 1200));
        Assert.Equal(actual: count, expected: 1);
        using (watch.RecordReads(path: root)) {
            Assert.True(condition: WorldCompileCache.Shared.TryCompile(compiled: out _, failure: out _, path: root));
        }
        File.WriteAllText(module, Module.Replace(comparisonType: StringComparison.Ordinal, newValue: "9", oldValue: "7"));
        watch.Poll(now: At(milliseconds: 2000));
        watch.Poll(now: At(milliseconds: 2300));
        Assert.Equal(actual: count, expected: 1);
        watch.Complete(result: CommandResult.None);
        watch.Poll(now: At(milliseconds: 2400));
        watch.Poll(now: At(milliseconds: 2550));
        Assert.Equal(actual: count, expected: 2);
        watch.Complete(result: CommandResult.Error(output: "source.puck:2: invalid row"));
        Assert.Contains("source.puck:2", watch.LastError!);
        watch.Stop();
        File.WriteAllText(contents: Module, path: module);
        watch.Poll(now: At(milliseconds: 3000));
        Assert.Equal(actual: count, expected: 2);
    }
    [Fact]
    public void AFailedReloadWatchesTheNewMissingImportAndALoadFollowsItsNewOrigin() {
        using var files = new TemporaryDirectory();
        var first = files.WriteText(name: "first.puck", text: "state { world { slot value = 1 } }");
        var current = first;
        var count = 0;
        var watch = new WorldSourceWatch(() => current);

        watch.Start(submitReload: () => count++);
        File.WriteAllText(contents: "import \"counter.puck\"\nuse counter as root()", path: first);
        watch.Poll(now: At(milliseconds: 1000));
        watch.Poll(now: At(milliseconds: 1150));
        Assert.Equal(actual: count, expected: 1);
        using (watch.RecordReads(path: first)) {
            Assert.False(condition: WorldCompileCache.Shared.TryCompile(compiled: out _, failure: out _, path: first));
        }
        watch.Complete(result: CommandResult.Error(output: "missing counter.puck"));
        files.WriteText(name: "counter.puck", text: Module);
        watch.Poll(now: At(milliseconds: 1200));
        watch.Poll(now: At(milliseconds: 1350));
        Assert.Equal(actual: count, expected: 2);
        watch.Complete(result: CommandResult.None);
        current = files.WriteText(name: "second.puck", text: "state { world { slot value = 2 } }");
        // The load accepted value=2; the source changed before the watch adopted that origin.
        File.WriteAllText(contents: "state { world { slot value = 20 } }", path: current);
        watch.Poll(now: At(milliseconds: 2000));
        watch.Poll(now: At(milliseconds: 2150));
        Assert.Equal(actual: count, expected: 3);
        watch.Complete(result: CommandResult.None);
        File.WriteAllText(contents: "state { world { slot value = 3 } }", path: first);
        watch.Poll(now: At(milliseconds: 2200));
        watch.Poll(now: At(milliseconds: 2400));
        Assert.Equal(actual: count, expected: 3);
        File.WriteAllText(contents: "state { world { slot value = 4 } }", path: current);
        watch.Poll(now: At(milliseconds: 2500));
        watch.Poll(now: At(milliseconds: 2650));
        Assert.Equal(actual: count, expected: 4);
    }
}
