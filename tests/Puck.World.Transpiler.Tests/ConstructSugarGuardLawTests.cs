using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>What the printer requires before it may print a node as its construct comes from that construct's
/// description: a node carrying a key the description has no place for takes the recorded fallback, and the same
/// node without that key sugars.</summary>
/// <remarks>The pair is the law. Sugaring alone would pass against a guard that admitted everything, and falling
/// back alone would pass against one that admitted nothing.</remarks>
public class ConstructSugarGuardLawTests {
    private const string Unspellable = "unspellable";

    private static string Decompile(JsonObject document) => WorldDecompiler.Decompile(root: document);
    private static JsonObject Compile(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation.RequireJson();
    }
    private static JsonObject NodeAt(JsonObject document, string path) {
        JsonNode? node = document;

        foreach (var segment in path.Split(separator: '/')) {
            node = (int.TryParse(s: segment, result: out var index)
                ? ((JsonArray)node!)[index]
                : ((JsonObject)node!)[propertyName: segment]
            );
        }

        return ((JsonObject)node!);
    }

    /// <summary>The source that authors each construct whose spelling admits a closed set of keys, the node it
    /// lowers to, and the text that proves the printer took the fallback instead.</summary>
    private static readonly Dictionary<string, (string Source, string Path, string Fallback)> Fixtures = new(comparer: StringComparer.Ordinal) {
        ["grid"] = (
            "schema: \"puck.world.definition.v1\"\n\nstate {\n    world {\n        grid board dimensions(width: 2, depth: 2)\n    }\n}\n",
            "state/world/0",
            "row {"
        ),
        ["pattern"] = (
            "schema: \"puck.world.definition.v1\"\n\npattern p : Int {\n    symbols {\n        a = 1\n    }\n    match: a\n}\n",
            "patterns/0",
            "patterns ["
        ),
        ["pile"] = (
            "schema: \"puck.world.definition.v1\"\n\nstate {\n    world {\n        table deck {\n            a = 1\n        }\n\n        pile stock of deck {\n            a\n        }\n    }\n}\n",
            "state/world/1",
            "row {"
        ),
        ["set"] = (
            "schema: \"puck.world.definition.v1\"\n\nset s: all\n",
            "sets/0",
            "sets ["
        ),
        ["slot"] = (
            "schema: \"puck.world.definition.v1\"\n\nstate {\n    world {\n        slot hp = 1\n    }\n}\n",
            "state/world/0",
            "row {"
        ),
        ["table"] = (
            "schema: \"puck.world.definition.v1\"\n\nstate {\n    world {\n        table bag {\n            a = 1\n        }\n    }\n}\n",
            "state/world/0",
            "row {"
        ),
    };

    /// <summary>Every construct whose spelling admits a closed set of keys, taken from the table: an open
    /// spelling prints an undescribed field as an ordinary property and has no such requirement.</summary>
    public static TheoryData<string> Closed() => new(values: WorldConstructs.Table.Constructs
        .Where(predicate: static construct => !construct.Sugar.Open)
        .Select(selector: static construct => construct.Keyword));

    [MemberData(nameof(Closed))]
    [Theory]
    public void ADescribedConstructSugarsAndAnUndescribedKeySendsItToItsFallback(string keyword) {
        Assert.True(
            condition: Fixtures.TryGetValue(
            key: keyword,
            value: out var fixture
        ),
            userMessage: $"'{keyword}' admits a closed set of keys and has no source here to prove it with"
        );

        var (source, path, fallback) = fixture;

        Assert.True(condition: WorldConstructs.Table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));
        Assert.DoesNotContain(
            expectedSubstring: Unspellable,
            actualString: string.Join(
                separator: " ",
                values: construct!.NodeKeys
            ),
            comparisonType: StringComparison.Ordinal
        );

        var sugared = Decompile(document: Compile(source: source));

        Assert.Contains(
            expectedSubstring: keyword,
            actualString: sugared,
            comparisonType: StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            expectedSubstring: fallback,
            actualString: sugared,
            comparisonType: StringComparison.Ordinal
        );

        var document = Compile(source: source);

        NodeAt(
            document: document,
            path: path
        )[Unspellable] = 1;

        var printed = Decompile(document: document);

        Assert.Contains(
            expectedSubstring: fallback,
            actualString: printed,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AShapeRowMissingARequiredKeyTakesTheFallback() {
        Assert.True(condition: WorldConstructs.Table.TryGet(
            construct: out var shape,
            keyword: "shape"
        ));
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: shape!.RequiredKeys
            ),
            expected: "type"
        );

        var document = Compile(source: "schema: \"puck.creation.v1\"\n\nshape Box \"b\" {\n    size [1, 1, 1]\n}\n");

        _ = NodeAt(
            document: document,
            path: "shapes/0"
        ).Remove(propertyName: "type");

        Assert.Contains(
            expectedSubstring: "shapes [",
            actualString: Decompile(document: document),
            comparisonType: StringComparison.Ordinal
        );
    }
}
