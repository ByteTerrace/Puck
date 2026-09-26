using Puck.Testing;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A module's relative file paths resolve beside the module that writes them, whichever source uses it, through the one
// relocation an asset path takes (WorldDocumentVocabulary.RelocateFileReference).
public sealed class FileReferenceRelocationLawTests {
    private const string Module = """
        module board() {
          views {
            graph "board" {
              source: "board.graph.json"
            }
          }
        }
        """;
    // The same file named through a `let`, and through a module argument written in the module's own directory.
    private const string CarriedModule = """
        let file = "board.graph.json"

        module board() {
          views {
            graph "board" {
              source: file
            }
          }
        }

        module named(graphFile) {
          views {
            graph "named" {
              source: graphFile
            }
          }
        }

        module wrapped() {
          use named(graphFile: "board.graph.json")
        }
        """;

    private static (string Path, System.Text.Json.Nodes.JsonObject Json) CompileRoot(TemporaryDirectory directory, string name, string import, string use = "use board()") {
        var source = $"""
            schema: "puck.world.definition.v1"
            import "{import}"
            {use}
            """;
        var path = directory.WriteBytes(bytes: [], name: name);
        var compilation = WorldCompiler.Compile(
            source: source,
            sourcePath: path,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(sourceText: source));
        return (path, compilation.RequireJson());
    }
    private static string GraphSource(System.Text.Json.Nodes.JsonObject json) => json["views"]!["graphs"]![0]!["source"]!.GetValue<string>();

    [Fact]
    public void AModuleUsedFromTwoDirectoriesNamesOneFile() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: "modules/board.puck", text: Module);
        var graph = directory.WriteBytes(bytes: "{}"u8, name: "modules/board.graph.json");
        var near = CompileRoot(directory: directory, import: "../modules/board.puck", name: "near/world.puck");
        var far = CompileRoot(directory: directory, import: "../../modules/board.puck", name: "far/deeper/world.puck");
        var beside = CompileRoot(directory: directory, import: "board.puck", name: "modules/world.puck");

        Assert.Equal(expected: "../modules/board.graph.json", actual: GraphSource(json: near.Json));
        Assert.Equal(expected: "../../modules/board.graph.json", actual: GraphSource(json: far.Json));
        Assert.Equal(expected: "board.graph.json", actual: GraphSource(json: beside.Json));
        foreach (var (path, json) in new[] { near, far, beside }) {
            Assert.Equal(
                expected: Path.GetFullPath(path: graph),
                actual: Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: GraphSource(json: json)))
            );
        }
    }
    [InlineData("use board()")]
    [InlineData("use wrapped()")]
    [Theory]
    public void APathCarriedByALetOrAnArgumentNamesTheFileBesideItsLiteral(string use) {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: "modules/board.puck", text: CarriedModule);
        var graph = directory.WriteBytes(bytes: "{}"u8, name: "modules/board.graph.json");

        var (path, json) = CompileRoot(directory: directory, import: "../modules/board.puck", name: "root/world.puck", use: use);

        Assert.Equal(expected: "../modules/board.graph.json", actual: GraphSource(json: json));
        Assert.Equal(
            expected: Path.GetFullPath(path: graph),
            actual: Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: GraphSource(json: json)))
        );

        var printed = WorldDecompiler.Decompile(root: json);
        var recompiled = WorldCompiler.Compile(
            source: printed,
            sourcePath: path,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: recompiled.Diagnostics.HasErrors, userMessage: recompiled.Diagnostics.FormatReport(sourceText: printed));
        Assert.Equal(expected: json.ToJsonString(), actual: recompiled.RequireJson().ToJsonString());
    }
    [Fact]
    public void AnArgumentWrittenInTheUsingSourceNamesTheFileBesideIt() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: "modules/board.puck", text: CarriedModule);
        var (_, json) = CompileRoot(directory: directory, import: "../modules/board.puck", name: "root/world.puck", use: "use named(graphFile: \"local.graph.json\")");

        Assert.Equal(expected: "local.graph.json", actual: GraphSource(json: json));
    }
    [Fact]
    public void ARelocatedPathDecompilesAndRecompilesToItself() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: "modules/board.puck", text: Module);
        _ = directory.WriteBytes(bytes: "{}"u8, name: "modules/board.graph.json");
        var (path, json) = CompileRoot(directory: directory, import: "../modules/board.puck", name: "root/world.puck");
        var printed = WorldDecompiler.Decompile(root: json);
        var recompiled = WorldCompiler.Compile(
            source: printed,
            sourcePath: path,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: recompiled.Diagnostics.HasErrors, userMessage: recompiled.Diagnostics.FormatReport(sourceText: printed));
        Assert.Equal(expected: json.ToJsonString(), actual: recompiled.RequireJson().ToJsonString());
    }
}
