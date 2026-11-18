using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A save writes each live edit into the source that authors it, keeping every <c>let</c>, <c>for</c> and
/// comment around it, and writes nothing the simulation moves.</summary>
public sealed class SourceTreeSaveLawTests {
    private const string Root = (WorldSources.Header + """
        import "props"

        // The post stands this high.
        let height = 1.5

        placements {
          placement "post" {
            prototype: "post"
            position [0, height, 0]
          }
          placement "spare" {
            prototype: "post"
            position [3, 0, 3]
          }
        }

        state {
          world {
            slot score = 1
          }
        }

        """);
    private const string Props = (WorldSources.Header + """
        let lampX = 4

        placements {
          placement "lamp" {
            prototype: "post"
            position [lampX, 0, 0]
          }
          for i in range(0, 2) {
            placement $"fence-{i}" {
              prototype: "post"
              position [i, 0, 1]
            }
          }
        }

        """);

    // The tree rooted at `root` as a live world composes it, changed by `edit`.
    private static WorldDefinition Live(string root, Action<JsonObject> edit) {
        var compiled = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: File.ReadAllText(path: root), sourcePath: root);

        Assert.True(condition: WorldSourceLoader.TryReadAuthored(authored: out var authored, document: Encoding.UTF8.GetBytes(s: compiled.RequireJson().ToJsonString()), path: root, reason: out var reason), userMessage: reason);
        var document = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.SerializeBeside(definition: authored!, path: root))!.AsObject();

        edit(obj: document);
        Assert.True(condition: WorldDefinitionFileSource.TryParseDocument(definition: out var live, json: document.ToJsonString(), reason: out reason, sourceName: root), userMessage: reason);
        return (live! with { DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: root) });
    }
    private static JsonArray Rows(JsonObject document) => document["placements"]!["rows"]!.AsArray();
    private static JsonObject Row(JsonObject document, string id) => Rows(document: document).OfType<JsonObject>().Single(predicate: row => (row["id"]!.GetValue<string>() == id));

    [Fact]
    public void EachEditLandsInTheSourceThatAuthorsIt() {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "world.puck", text: Root);
        var props = files.WriteText(name: "props.puck", text: Props);
        var live = Live(root: root, edit: static document => {
            Row(document: document, id: "lamp")["position"]![0] = 5;
            Row(document: document, id: "post")["position"]![1] = 2;
            _ = Rows(document: document).Remove(item: Row(document: document, id: "spare"));
            Rows(document: document).Add(item: new JsonObject { ["id"] = "added", ["prototypeId"] = "post", ["position"] = new JsonArray(9, 0, 9), ["yawDegrees"] = 0, ["scale"] = 1 });
            document["state"]!["world"]![0]!["value"] = 7;
        });

        Assert.True(condition: WorldSourceSave.TrySave(bytesWritten: out var bytes, definition: live, path: root, reason: out var reason, written: out var written), userMessage: reason);

        Assert.Equal(expected: [Puck.Abstractions.PuckPaths.Normalize(path: props), Puck.Abstractions.PuckPaths.Normalize(path: root)], actual: written);
        Assert.Equal(expected: (new FileInfo(fileName: props).Length + new FileInfo(fileName: root).Length), actual: bytes);
        Assert.Equal(expected: Props.Replace(oldValue: "let lampX = 4", newValue: "let lampX = 5", comparisonType: StringComparison.Ordinal), actual: File.ReadAllText(path: props));
        Assert.Equal(
            actual: File.ReadAllText(path: root),
            expected: Root
                .Replace(oldValue: "let height = 1.5", newValue: "let height = 2", comparisonType: StringComparison.Ordinal)
                .Replace(oldValue: "  placement \"spare\" {\n    prototype: \"post\"\n    position [3, 0, 3]\n  }\n", newValue: "", comparisonType: StringComparison.Ordinal)
                .Replace(oldValue: "    position [0, height, 0]\n  }\n}", newValue: "    position [0, height, 0]\n  }\n  placement \"added\" {\n    prototype: \"post\"\n    position [9, 0, 9]\n  }\n}", comparisonType: StringComparison.Ordinal)
        );
    }
    [Fact]
    public void ALetTwoNodesReadStaysAndTheEditIsWrittenAtItsNode() {
        using var files = new TemporaryDirectory();
        var source = (WorldSources.Header + """
            let x = 1

            placements {
              placement "a" {
                prototype: "post"
                position [x, 0, 0]
              }
              placement "b" {
                prototype: "post"
                position [x, 0, 1]
              }
            }

            """);
        var root = files.WriteText(name: "world.puck", text: source);
        var live = Live(root: root, edit: static document => Row(document: document, id: "a")["position"]![0] = 3);

        Assert.True(condition: WorldSourceSave.TrySave(bytesWritten: out _, definition: live, path: root, reason: out var reason, written: out _), userMessage: reason);
        var saved = File.ReadAllText(path: root);

        Assert.Contains(actualString: saved, expectedSubstring: "let x = 1");
        Assert.Contains(actualString: saved, expectedSubstring: "position [x, 0, 1]");
        Assert.DoesNotContain(actualString: saved, expectedSubstring: "position [x, 0, 0]");
    }
    [Fact]
    public void AnEditToARowAForWritesRefusesByNameAndWritesNothing() {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "world.puck", text: Root);
        var props = files.WriteText(name: "props.puck", text: Props);
        var live = Live(root: root, edit: static document => Row(document: document, id: "fence-0")["position"]![0] = 8);

        Assert.False(condition: WorldSourceSave.TrySave(bytesWritten: out var bytes, definition: live, path: root, reason: out var reason, written: out var written));
        Assert.Contains(actualString: reason, expectedSubstring: "fence-0");
        Assert.Contains(actualString: reason, expectedSubstring: "compile-time for");
        Assert.Equal(expected: 0, actual: bytes);
        Assert.Empty(collection: written);
        Assert.Equal(expected: Props, actual: File.ReadAllText(path: props));
        Assert.Equal(expected: Root, actual: File.ReadAllText(path: root));
    }
    [Fact]
    public void ASourceThatDoesNotCompileIsNotSavedInto() {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "world.puck", text: Root);
        _ = files.WriteText(name: "props.puck", text: Props);
        var live = Live(root: root, edit: static _ => { });

        _ = files.WriteText(name: "world.puck", text: (Root + "placements {\n"));
        Assert.False(condition: WorldSourceSave.TrySave(bytesWritten: out _, definition: live, path: root, reason: out var reason, written: out _));
        Assert.Contains(actualString: reason, expectedSubstring: "does not compile");
    }
    [Fact]
    public void ANewPathIsPrintedWholeFromTheSnapshot() {
        using var files = new TemporaryDirectory();
        var root = files.WriteText(name: "world.puck", text: Root);
        _ = files.WriteText(name: "props.puck", text: Props);
        var live = Live(root: root, edit: static _ => { });
        var fresh = Path.Combine(path1: files.RootPath, path2: "fresh.puck");

        Assert.True(condition: WorldSourceSave.TrySave(bytesWritten: out var bytes, definition: live, path: fresh, reason: out var reason, written: out var written), userMessage: reason);
        Assert.Equal(expected: new FileInfo(fileName: fresh).Length, actual: bytes);
        Assert.Equal(expected: [Puck.Abstractions.PuckPaths.Normalize(path: fresh)], actual: written);
        Assert.Contains(actualString: File.ReadAllText(path: fresh), expectedSubstring: "placement \"fence-1\"");
    }
}
