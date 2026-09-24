using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document composed over a basis is written and refused as its own source: an enum member it writes reads
/// as its basis reads it, a compile of it rests on the basis it read, and a refusal of one of its rows lands on that
/// row's line however many rows the basis contributes ahead of it.</summary>
public sealed class BasisCompositionLawTests {
    private const string Element = "enum Element {\n    Nothing\n    Air\n    Water\n    Fire\n    Earth\n}\n";
    private const string Suit = "enum Suit {\n    Clubs\n    Hearts\n}\n";

    private static string Basis(string enums, int rows = 0) {
        var world = new StringBuilder();

        for (var index = 0; (index < rows); index++) {
            _ = world.Append(value: $"        slot base{index} = {index}\n");
        }

        return $"{WorldSources.Header}state {{\n{enums}    world {{\n{world}    }}\n}}\n";
    }
    private static WorldCompilation Compile(string path, SourceMap? sourceMap = null) => WorldCompiler.Compile(
        cancellationToken: TestContext.Current.CancellationToken,
        source: File.ReadAllText(path: path),
        sourceMap: sourceMap,
        sourcePath: path
    );
    private static JsonObject Row(JsonObject document, string name) => document["state"]!["world"]!.AsArray()
        .OfType<JsonObject>()
        .Single(predicate: row => (row["name"]?.ToString() == name));
    private static (DiagnosticBag Diagnostics, string Source) Diagnose(string path) {
        var source = File.ReadAllText(path: path);
        var sourceMap = new SourceMap();
        var compilation = Compile(
            path: path,
            sourceMap: sourceMap
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        _ = WorldSemanticValidator.ValidateComposedWorld(
            diagnostics: compilation.Diagnostics,
            loweredJson: compilation.RequireJson(),
            sourceMap: sourceMap,
            sourcePath: path
        );

        return (compilation.Diagnostics, source);
    }

    // A slot, a table and a record's default and seed may each name a member of an enum only the basis declares; the
    // basis carries the enum into the composed `state.enums`, and the child's own document does not repeat it.
    [Fact]
    public void AChildWritesAMemberOfAnEnumItsBasisDeclares() {
        using var files = new TemporaryDirectory();

        _ = files.WriteText(name: "base.puck", text: Basis(enums: (Element + Suit)));

        var child = files.WriteText(
            name: "child.puck",
            text: "basis: \"base\"\n\nstate {\n    record Card {\n        suit: Suit = Hearts\n    }\n    pool cards of Card capacity(1) = [\n        { suit: Suit.Clubs }\n    ]\n    world {\n        slot left: Element = Air\n        table recipe: Element {\n            a = Fire\n        }\n    }\n}\n"
        );

        var (diagnostics, source) = Diagnose(path: child);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );

        var document = Compile(path: child).RequireJson();

        Assert.Equal(expected: 1L, actual: Row(document: document, name: "left")["value"]!.GetValue<long>());
        Assert.Equal(expected: 3L, actual: Row(document: document, name: "recipe")["cells"]![0]!["value"]!.GetValue<long>());
        Assert.Null(@object: document["state"]!["enums"]);
    }
    // A basis member spelled like one of the child's own names cannot also read bare, and the one place in the child
    // that member comes from is its `basis` line.
    [Fact]
    public void ABasisMemberSpelledLikeAChildsLetIsRefusedAtTheBasisLine() {
        using var files = new TemporaryDirectory();

        _ = files.WriteText(name: "base.puck", text: Basis(enums: Element));

        var child = files.WriteText(
            name: "child.puck",
            text: "basis: \"base\"\n\nlet Air = 7\n\nstate {\n    world {\n        slot left: Element = Element.Water\n    }\n}\n"
        );
        var compilation = Compile(path: child);

        WorldSources.AssertRefusedAt(
            code: PuckDiagnosticCodes.EnumMemberShadowsConstant,
            diagnostics: compilation.Diagnostics,
            label: "a basis member spelled like a let",
            mentions: "(declared by the basis)",
            needle: "basis:",
            source: File.ReadAllText(path: child)
        );
    }
    // The child's compile rests on the basis it read the enums from, even when that basis was served from the cache
    // rather than compiled: reordering the basis's enum recompiles the child, which reads the member anew.
    [Fact]
    public void AChildRecompilesWhenItsBasisEnumMoves() {
        using var files = new TemporaryDirectory();

        var basis = files.WriteText(name: "base.puck", text: Basis(enums: Element));
        var child = files.WriteText(
            name: "child.puck",
            text: "basis: \"base\"\n\nstate {\n    world {\n        slot left: Element = Air\n    }\n}\n"
        );
        var cache = new WorldCompileCache();

        Assert.True(condition: WorldCompileCache.Shared.TryCompile(compiled: out _, failure: out var basisFailure, path: basis), userMessage: basisFailure?.Diagnostics.FormatReport(filePath: basis));
        Assert.True(condition: cache.TryCompile(compiled: out var first, failure: out var childFailure, path: child), userMessage: childFailure?.Diagnostics.FormatReport(filePath: child));
        Assert.Equal(
            expected: 1L,
            actual: Row(document: JsonNode.Parse(utf8Json: first!.Document)!.AsObject(), name: "left")["value"]!.GetValue<long>()
        );

        _ = files.WriteText(name: "base.puck", text: Basis(enums: "enum Element {\n    Air\n    Nothing\n}\n"));

        Assert.True(condition: cache.TryCompile(compiled: out var second, failure: out var secondFailure, path: child), userMessage: secondFailure?.Diagnostics.FormatReport(filePath: child));
        Assert.Equal(
            expected: 0L,
            actual: Row(document: JsonNode.Parse(utf8Json: second!.Document)!.AsObject(), name: "left")["value"]!.GetValue<long>()
        );
    }
    // Two sources that each name the other as a basis compile rather than read each other's enums without end; their
    // composition refuses the cycle by name.
    [Fact]
    public void ABasisCycleCompiles() {
        using var files = new TemporaryDirectory();

        _ = files.WriteText(name: "b.puck", text: $"basis: \"a\"\n\nstate {{\n{Element}    world {{\n        slot b = 0\n    }}\n}}\n");

        var a = files.WriteText(name: "a.puck", text: $"basis: \"b\"\n\nstate {{\n{Suit}    world {{\n        slot a = 0\n    }}\n}}\n");
        var compilation = Compile(path: a);

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(File.ReadAllText(path: a))
        );
    }
    public static TheoryData<int, int, string> Offsets() {
        var data = new TheoryData<int, int, string>();

        foreach (var basisRows in ((int[])[0, 1, 3])) {
            foreach (var row in ((int[])[0, 2])) {
                data.Add(p1: basisRows, p2: row, p3: "slot");
                data.Add(p1: basisRows, p2: row, p3: "row");
            }
        }

        return data;
    }
    // The validation reads the composed document, where the basis's rows come first; the source map indexes the
    // child's own rows. A refusal of the child's k-th row lands on that row's line whatever N rows the basis
    // contributes, whether the validator refuses it for an undeclared enum (PUCK119) or anything else (PUCK030).
    [MemberData(nameof(Offsets))]
    [Theory]
    public void ARefusalOfAChildsRowLandsOnThatRowsLine(int basisRows, int row, string kind) {
        using var files = new TemporaryDirectory();

        _ = files.WriteText(name: "base.puck", text: Basis(enums: string.Empty, rows: basisRows));

        var world = new StringBuilder();

        for (var index = 0; (index < 4); index++) {
            _ = world.Append(value: ((index != row)
                ? $"        slot child{index} = {index}\n"
                : ((kind == "slot")
                    ? $"        slot child{index}: Missing = 0\n"
                    : $"        row {{\n            name: \"child{index}\"\n            kind: Bool\n            value: false\n            min: 0\n        }}\n")));
        }

        var child = files.WriteText(
            name: "child.puck",
            text: $"basis: \"base\"\n\nstate {{\n    world {{\n{world}    }}\n}}\n"
        );

        var (diagnostics, source) = Diagnose(path: child);

        WorldSources.AssertRefusedAt(
            code: ((kind == "slot") ? PuckDiagnosticCodes.StateEnumUndeclared : PuckDiagnosticCodes.SemanticValidation),
            diagnostics: diagnostics,
            label: $"{kind} {row} after {basisRows} basis rows",
            needle: ((kind == "slot") ? $"slot child{row}: Missing" : "row {"),
            source: source
        );
    }
}
