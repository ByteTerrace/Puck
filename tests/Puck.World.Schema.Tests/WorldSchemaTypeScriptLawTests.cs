using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// <see cref="WorldSchema.ToTypeScript"/>, the TypeScript <c>puck schema</c> writes for the dashboard portal: each
/// JSON Schema keyword maps to the TypeScript type it means, every bundle definition gets its own named export, and a
/// schema the emitter cannot map refuses by name rather than widening to <c>unknown</c>.
/// </summary>
public sealed partial class WorldSchemaTypeScriptLawTests {
    private static readonly Lazy<string> Shipped = new(valueFactory: static () => WorldSchema.ToTypeScript(bundle: WorldSchema.Bundle(split: WorldSchema.Export(postProcessPackages: []))));

    [GeneratedRegex(pattern: "^export type (\\w+) = ", options: RegexOptions.Multiline)]
    private static partial Regex ExportName();
    private static string Emit(string defs, string root = """{"type":"object","additionalProperties":false}""") {
        var bundle = JsonNode.Parse(json: root)!.AsObject();

        bundle["$defs"] = JsonNode.Parse(json: defs);

        return WorldSchema.ToTypeScript(bundle: bundle);
    }
    private static string Declaration(string text, string name) {
        var start = text.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: $"export type {name} = "
        );

        Assert.True(
            condition: (start >= 0),
            userMessage: $"no export named {name}"
        );

        var end = text.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: start,
            value: ";\n\n"
        );

        return text[start..((end < 0)
            ? text.Length
            : (end + 1)
        )];
    }

    [Fact]
    public void EveryBundleDefinitionAndTheRootGetOneNamedExport() {
        var bundle = WorldSchema.Bundle(split: WorldSchema.Export(postProcessPackages: []));
        var exports = ExportName().Matches(input: Shipped.Value).Select(selector: static match => match.Groups[1].Value).ToList();
        var expected = ((JsonObject)bundle["$defs"]!).Select(selector: static pair => pair.Key).Append(element: WorldSchema.TypeScriptRootName).Order(comparer: StringComparer.Ordinal).ToList();

        Assert.Equal(
            actual: exports.Order(comparer: StringComparer.Ordinal).ToList(),
            expected: expected
        );
        Assert.StartsWith(
            actualString: Shipped.Value,
            expectedStartString: "// GENERATED FILE — do not hand-edit.\n"
        );
        Assert.Contains(
            expectedSubstring: $"// Source bundle: schemaVersion={WorldSchema.SchemaId} generator=Puck.World.WorldSchema\n",
            actualString: Shipped.Value
        );
    }
    [Fact]
    public void TheStateSectionTypesItsRowsAndARowItsKindAndCells() {
        var text = Shipped.Value;

        Assert.Contains(
            expectedSubstring: "  world?: WorldStateRow[] | null;\n",
            actualString: Declaration(name: "WorldStateSection", text: text)
        );

        var row = Declaration(name: "WorldStateRow", text: text);

        Assert.Contains(
            expectedSubstring: $"  kind: {string.Join(separator: " | ", values: Enum.GetNames<Puck.State.CellKind>().Select(selector: static name => $"\"{name}\""))};\n",
            actualString: row
        );
        Assert.Matches(
            actualString: row,
            expectedRegexPattern: "  cells\\?: \\{\\n    key: string;\\n    value: \\w+;\\n"
        );
    }
    [Fact]
    public void ADiscriminatedUnionListsItsArmsWithNullLast() {
        var text = Emit(defs: """
            {
                "A": {"type":"object","properties":{"$type":{"const":"a"}},"additionalProperties":false},
                "B": {"type":"object","properties":{"$type":{"const":"b"}},"additionalProperties":false},
                "U": {"type":"object","required":["$type"],"anyOf":[{"anyOf":[{"$ref":"#/$defs/A"},{"type":"null"}]},{"anyOf":[{"$ref":"#/$defs/B"},{"type":"null"}]}]}
            }
            """);

        Assert.Contains(
            actualString: text,
            expectedSubstring: "export type U = A | B | null;\n"
        );
        Assert.Contains(
            actualString: text,
            expectedSubstring: "export type A = {\n  $type?: \"a\";\n};\n"
        );
    }
    [Fact]
    public void RequiredAdditionalPropertiesAndTuplesMapToTheirTypeScriptMeaning() {
        var text = Emit(defs: """
            {
                "Closed": {"type":"object","properties":{"id":{"type":"integer"},"note":{"type":["string","null"]}},"required":["id"],"additionalProperties":false},
                "Open": {"type":"object","properties":{"id":{"type":"integer"}}},
                "Map": {"type":"object","additionalProperties":{"type":"boolean"}},
                "Empty": {"type":"object","additionalProperties":false},
                "Vector": {"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3},
                "List": {"type":["array","null"],"items":{"anyOf":[{"$ref":"#/$defs/Closed"},{"type":"null"}]}},
                "Tag": {"enum":["a\"b","c\\d",1,true,null]},
                "Hidden": {"title":"Hidden","not":{"type":"null"}},
                "Conditional": {"type":"object","properties":{"id":{"type":"string"}},"additionalProperties":false,"allOf":[{"if":{"required":["id"]},"then":{"required":["id"]}}]}
            }
            """);

        Assert.Contains(actualString: text, expectedSubstring: "export type Closed = {\n  id: number;\n  note?: string | null;\n};\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Open = {\n  id?: number;\n  [k: string]: unknown;\n};\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Map = {\n  [k: string]: boolean;\n};\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Empty = Record<string, never>;\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Vector = [number, number, number];\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type List = (Closed | null)[] | null;\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Tag = \"a\\\"b\" | \"c\\\\d\" | 1 | true | null;\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Hidden = unknown;\n");
        Assert.Contains(actualString: text, expectedSubstring: "export type Conditional = {\n  id?: string;\n};\n");
    }
    [Fact]
    public void ADescriptionBecomesADocCommentThatCannotCloseItself() {
        var text = Emit(defs: """
            {"Noted": {"description":"Ends */ early.\nSecond line.","type":"object","properties":{"id":{"description":"The id.","type":"string"}},"additionalProperties":false}}
            """);

        Assert.Contains(
            actualString: text,
            expectedSubstring: "/**\n * Ends *\\/ early.\n * Second line.\n */\nexport type Noted = {\n  /**\n   * The id.\n   */\n  id?: string;\n};\n"
        );
    }
    [Fact]
    public void AnUnmappedKeywordOrADanglingReferenceRefusesByName() {
        var keyword = Assert.Throws<InvalidOperationException>(testCode: () => Emit(defs: """{"Pair": {"type":"array","prefixItems":[{"type":"number"}]}}"""));

        Assert.Contains(expectedSubstring: "#/$defs/Pair", actualString: keyword.Message);
        Assert.Contains(expectedSubstring: "'prefixItems'", actualString: keyword.Message);

        var dangling = Assert.Throws<InvalidOperationException>(testCode: () => Emit(defs: """{"Holder": {"$ref":"#/$defs/Missing"}}"""));

        Assert.Contains(expectedSubstring: "#/$defs/Missing", actualString: dangling.Message);

        var root = Assert.Throws<InvalidOperationException>(testCode: () => Emit(defs: (("{\"" + WorldSchema.TypeScriptRootName) + "\": {\"type\":\"string\"}}")));

        Assert.Contains(expectedSubstring: WorldSchema.TypeScriptRootName, actualString: root.Message);
    }
    [Fact]
    public void TheSameBundleEmitsTheSameText() {
        var bundle = WorldSchema.Bundle(split: WorldSchema.Export(postProcessPackages: []));

        Assert.Equal(
            actual: WorldSchema.ToTypeScript(bundle: bundle),
            expected: Shipped.Value
        );
    }
}
