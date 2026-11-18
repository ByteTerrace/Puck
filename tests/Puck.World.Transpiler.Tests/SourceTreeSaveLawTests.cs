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
    public void PrivateHandsAndDeckShareOneDurableSaveEvenWhenGeneratedInAnImport() {
        using var files = new TemporaryDirectory();
        const string Cards = """
            state { world {
              row { name:"tokens" kind:Int cells [{key:"a",value:1},{key:"b",value:2}] }
              pile deck of tokens capacity(2) { a b }
              for n in ["hand"] {
                row { name:n kind:Bool capacity:2 domain:keysOf(row:tokens, ordered:true) }
              }
            } }
            """;
        var module = files.WriteText("cards.puck", Cards);
        var root = files.WriteText("table.puck", WorldSources.Header + """
            import "cards"
            let rate = 30
            simulation { rateHz: rate }
            state { world {
              row { name:"entropy" kind:Int draw { privateStream:true timing:Event generator { source:StreamDraw } } }
            } }
            """);
        var authored = Live(root, document => document["simulation"]!["rateHz"] = 60);
        WorldDefinition Deal(bool second) => authored.WithWorldState(authored.AuthoredState.Select(row => row.Name.Value switch {
            "deck" => row with { Cells = second ? [] : [new(CellName.Parse("b"), CellValue.Bool(true))] },
            "hand" => row with { Cells = second ? [new(CellName.Parse("a"), CellValue.Bool(true)), new(CellName.Parse("b"), CellValue.Bool(true))] :
                [new(CellName.Parse("a"), CellValue.Bool(true))] },
            _ => row
        }).ToArray());
        Assert.True(WorldSourceSave.TrySave(Deal(false), root, out _, out var reason), reason);
        var save = WorldDefinitionFileSource.SourceSavePath(root);
        var committed = File.ReadAllBytes(save);
        if (OperatingSystem.IsWindows()) {
            using var held = File.Open(save, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<IOException>(() => WorldSourceSave.TrySave(Deal(true), root, out _, out _));
        } else {
            using var interrupted = Puck.Confinement.AtomicFile.StageAllBytes(save, WorldDefinitionSerialization.SerializeBeside(Deal(true), root));
        }
        Assert.Equal(committed, File.ReadAllBytes(save));
        Assert.Equal(Cards, File.ReadAllText(module));
        Assert.Contains("let rate = 60", File.ReadAllText(root));
        Assert.True(WorldDefinitionLoader.TryLoadFileForAdmission(save, out var loaded, out _, out reason, keys: WorldDrawSampler.Preview), reason);
        var sourceRows = Live(root, _ => { }).AuthoredState;
        Assert.Equal(2, sourceRows.Single(r => r.Name.Value == "deck").Cells!.Count);
        Assert.Empty(sourceRows.Single(r => r.Name.Value == "hand").Cells ?? []);
        var rows = loaded!.Definition.AuthoredState;
        Assert.Equal("b", Assert.Single(rows.Single(r => r.Name.Value == "deck").Cells!).Key.Value);
        Assert.Equal("a", Assert.Single(rows.Single(r => r.Name.Value == "hand").Cells!).Key.Value);

        var fresh = files.PathOf("fresh.puck");
        Assert.True(WorldSourceSave.TrySave(Deal(false), fresh, out _, out reason), reason);
        Assert.True(WorldSourceSave.TrySave(Deal(true), fresh, out _, out reason), reason);
        var compiled = WorldCompiler.Compile(source: File.ReadAllText(fresh), sourcePath: fresh, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(WorldSourceLoader.TryLoadForAdmission(fresh, Encoding.UTF8.GetBytes(compiled.RequireJson().ToJsonString()),
            out var resumed, out reason, keys: WorldDrawSampler.Preview), reason);
        Assert.Equal(60, resumed!.Definition.SimulationRateHz);
        Assert.Equal(2, resumed.Definition.AuthoredState.Single(r => r.Name.Value == "hand").Cells!.Count);
    }

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
            actual: File.ReadAllText(path: root).ReplaceLineEndings("\n"),
            expected: Root.ReplaceLineEndings("\n")
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
