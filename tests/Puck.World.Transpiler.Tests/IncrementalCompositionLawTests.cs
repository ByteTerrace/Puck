using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// Incremental equals clean: a document recomposed from the layers its last composition held, after an edit, is the
// document a composition holding nothing composes from the same files, refusals included, over an edit corpus that
// reaches every way a layer can change (a value, the own body, the basis, a new root member, a null, a collision) and
// over a one-colour edit in the debug world, where the edit also leaves the root's held compile standing.
public sealed class IncrementalCompositionLawTests {
    private sealed record Composition(bool Composed, string? Json, string Hash, string Reason, long Members);

    private static Composition Compose(WorldCompileSession session, string root, byte[] bytes) {
        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);
        var composed = WorldDefinitionFileSource.TryComposeChainBytes(composedJson: out var json, contentPin: out var pin, reason: out var reason, rootBytes: bytes, rootResolvedName: root, source: session);

        return new Composition(
            Composed: composed,
            Hash: pin,
            Json: ((json is null) ? null : System.Text.Encoding.UTF8.GetString(bytes: json)),
            Members: work.Read(kind: WorldBootWork.CompositionMembers),
            Reason: reason
        );
    }
    // The incremental composition, then the clean one from the same files, which must agree; returns the incremental.
    private static Composition Agree(WorldCompileSession session, string root, byte[] bytes) {
        var incremental = Compose(session: session, root: root, bytes: bytes);

        WorldDefinitionFileSource.ForgetComposedDocuments();

        var clean = Compose(session: session, root: root, bytes: bytes);

        Assert.Equal(expected: clean.Composed, actual: incremental.Composed);
        Assert.Equal(expected: clean.Reason, actual: incremental.Reason);
        Assert.Equal(expected: clean.Json, actual: incremental.Json);
        Assert.Equal(expected: clean.Hash, actual: incremental.Hash);
        // The clean composition holds the layers the next edit recomposes from.
        return incremental;
    }

    [Fact]
    public void ARecompositionByLayerIsTheCleanComposition() {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "root.world.json", text: "{\"basis\":\"base\",\"imports\":[{\"document\":\"a\"},{\"document\":\"b\"}],\"documentId\":\"root\",\"lights\":[{\"id\":\"lamp\",\"color\":\"red\"}]}");

        files.WriteText(name: "base.world.json", text: "{\"simulation\":{\"rateHz\":60},\"lights\":[{\"id\":\"sun\",\"color\":\"white\"}]}");
        files.WriteText(name: "a.world.json", text: "{\"sky\":{\"tint\":1},\"rows\":[{\"id\":\"r1\",\"v\":1}]}");
        files.WriteText(name: "b.world.json", text: "{\"rows\":[{\"id\":\"r2\",\"v\":2}],\"misc\":{\"x\":1}}");

        var session = new WorldCompileSession();
        var first = Agree(session: session, root: root, bytes: File.ReadAllBytes(path: root));
        var members = JsonNode.Parse(json: first.Json!)!.AsObject().Count;

        void Edit(string name, string from, string to) {
            var path = files.PathOf(name: name);
            var text = File.ReadAllText(path: path);

            Assert.Contains(expectedSubstring: from, actualString: text);
            File.WriteAllText(path: path, contents: text.Replace(oldValue: from, newValue: to));
        }
        long Step(string name, string from, string to) {
            Edit(from: from, name: name, to: to);

            var step = Agree(session: session, root: root, bytes: File.ReadAllBytes(path: root));

            Assert.True(condition: step.Composed, userMessage: step.Reason);

            return step.Members;
        }

        Assert.Equal(expected: members, actual: first.Members);
        // A value an import writes merges its one member again; so does a row two imports share, a value in the own
        // body, and a value in the basis.
        Assert.Equal(expected: 1, actual: Step(from: "\"tint\":1", name: "a.world.json", to: "\"tint\":2"));
        Assert.Equal(expected: 1, actual: Step(from: "\"v\":2", name: "b.world.json", to: "\"v\":3"));
        Assert.Equal(expected: 1, actual: Step(from: "\"red\"", name: "root.world.json", to: "\"blue\""));
        Assert.Equal(expected: 1, actual: Step(from: "\"rateHz\":60", name: "base.world.json", to: "\"rateHz\":120"));
        // A new root member, and a member that turns null, merge whole.
        Assert.Equal(expected: (members + 1), actual: Step(from: "\"sky\":{", name: "a.world.json", to: "\"fog\":{\"density\":1},\"sky\":{"));
        Assert.Equal(expected: members, actual: Step(from: "\"misc\":{\"x\":1}", name: "b.world.json", to: "\"misc\":null"));
        // An edit that makes two imports author one row differently refuses by name, as a clean composition refuses it.
        Edit(from: "{\"id\":\"r1\",\"v\":1}", name: "a.world.json", to: "{\"id\":\"r1\",\"v\":1},{\"id\":\"r2\",\"v\":9}");

        var refused = Agree(session: session, root: root, bytes: File.ReadAllBytes(path: root));

        Assert.False(condition: refused.Composed);
    }
    [Fact]
    public void AOneColourEditInTheDebugWorldKeepsTheRootCompileAndRecomposesOnlyWhatItWrites() {
        using var files = new TemporaryDirectory();
        var worlds = files.PathOf(name: "worlds");

        foreach (var file in Directory.EnumerateFiles(path: ShippedWorlds.FindDirectory(), searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: worlds, path2: Path.GetRelativePath(path: file, relativeTo: ShippedWorlds.FindDirectory()));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(sourceFileName: file, destFileName: target);
        }

        var debug = Path.Combine(path1: worlds, path2: "debug", path3: "debug.puck");
        var session = new WorldCompileSession();

        Assert.True(condition: session.TryCompile(path: debug, compiled: out var before, failure: out var failure), userMessage: failure?.Diagnostics.FormatReport(File.ReadAllText(path: debug)));

        var first = Agree(session: session, root: debug, bytes: before!.Document!);
        var day = Path.Combine(path1: worlds, path2: "debug", path3: "day.puck");
        var text = File.ReadAllText(path: day);

        Assert.Contains(expectedSubstring: "moonColor: \"#7088E8\"", actualString: text);
        File.WriteAllText(path: day, contents: text.Replace(oldValue: "moonColor: \"#7088E8\"", newValue: "moonColor: \"#7088E9\""));

        var work = new WorldBootWork();
        WorldCompiledSource? held;

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(condition: session.TryCompile(path: debug, compiled: out held, failure: out failure), userMessage: failure?.Diagnostics.FormatReport(File.ReadAllText(path: debug)));
        }

        // The root resolves the edited source's name and composes it at load, so its held compile stands, and it is
        // the compile it would be from nothing.
        Assert.Equal(expected: 0, actual: work.Read(kind: WorldBootWork.Compiles));
        Assert.Equal(expected: WorldCompiledSource.From(compilation: session.CompileFile(allowMultiple: true, cancellationToken: TestContext.Current.CancellationToken, includeTests: false, path: debug)).Document, actual: held!.Document);

        var edited = Agree(session: session, root: debug, bytes: held.Document!);

        Assert.NotEqual(expected: first.Json, actual: edited.Json);
        Assert.InRange(actual: edited.Members, high: 8, low: 1);
    }
}
