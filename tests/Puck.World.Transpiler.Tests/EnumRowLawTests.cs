using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A state row draws its cells from an enum the way a record field does: the row names it, and the enum
/// reaches <c>state.enums</c> through the one materialization a record field uses. The explicit <c>row { enum: }</c>
/// and the <c>: Enum</c> spelling of a <c>table</c>, <c>slot</c> or <c>grid</c> lower alike, and an enum no
/// declaration supplies is refused once, with PUCK119, on the line that names it. A row's enum is refused by
/// whole-document validation, the one refusal a JSON document draws too, since only the composed document says whether
/// a basis declares it.</summary>
public sealed class EnumRowLawTests {
    private const string Element = "enum Element {\n    Nothing\n    Air\n    Water\n    Fire\n    Earth\n}\n\n";

    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["an explicit row naming an undeclared enum"] = new(
            Body: "state {\n    world {\n        row {\n            name: \"recipe\"\n            kind: Int\n            enum: Elements\n        }\n    }\n}\n",
            Code: PuckDiagnosticCodes.StateEnumUndeclared,
            Needle: "enum: Elements"
        ) { Alone = true, Mentions = "'Elements'" },
        ["a slot naming an undeclared enum"] = new(
            Body: $"state {{\n    {Element}    world {{\n        slot left: Elements = 0\n    }}\n}}\n",
            Code: PuckDiagnosticCodes.StateEnumUndeclared,
            Needle: "slot left: Elements"
        ) { Alone = true },
        ["a table naming an undeclared enum"] = new(
            Body: "state {\n    world {\n        table recipe: Elements {\n            a = 1\n        }\n    }\n}\n",
            Code: PuckDiagnosticCodes.StateEnumUndeclared,
            Needle: "table recipe: Elements"
        ) { Alone = true },
        ["a record field naming an undeclared enum"] = new(
            Body: "state {\n    record Card {\n        suit: Suits\n    }\n    pool cards of Card capacity(1)\n}\n",
            Code: PuckDiagnosticCodes.StateEnumUndeclared,
            Needle: "suit: Suits"
        ) { Alone = true },
        ["a slot naming its enum as a modifier"] = new(
            Body: $"state {{\n    {Element}    world {{\n        slot left = Nothing enum(Element)\n    }}\n}}\n",
            Code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
            Needle: "enum(Element)"
        ) { Alone = true, Mentions = "'slot left: Enum'" },
        ["a grid naming its enum as a modifier"] = new(
            Body: $"state {{\n    {Element}    world {{\n        grid field dimensions(width: 2, depth: 2) enum(Element)\n    }}\n}}\n",
            Code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
            Needle: "enum(Element)"
        ) { Alone = true, Mentions = "'grid field: Enum'" },
        ["a table naming a record"] = new(
            Body: "state {\n    record Card {\n        rank: Int\n    }\n    world {\n        table cards: Card {\n            a = 1\n        }\n    }\n}\n",
            Code: PuckDiagnosticCodes.StateDeclarationKindAnnotated,
            Needle: "table cards: Card"
        ) { Alone = true, Mentions = "pool cards of Card capacity(...)" },
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);

    private static JsonObject Row(JsonObject document, string name) => document["state"]!["world"]!.AsArray()
        .OfType<JsonObject>()
        .Single(predicate: row => (row["name"]?.ToString() == name));
    private static void AssertEnumDeclared(JsonObject document) {
        var declared = Assert.Single(collection: document["state"]!["enums"]!.AsArray());

        Assert.Equal(
            expected: "Element",
            actual: declared!["name"]?.ToString()
        );
        Assert.Equal(
            expected: ["Nothing", "Air", "Water", "Fire", "Earth"],
            actual: declared["members"]!.AsArray().Select(selector: static member => member!.ToString())
        );
    }

    [Fact]
    public void AnExplicitRowNamingAnEnumDeclaresIt() {
        var document = WorldSources.LowerClean(body: $$"""
            state {
                {{Element}}
                world {
                    row {
                        name: "recipe"
                        kind: Int
                        enum: Element
                        cells [
                            { key: "a", value: Fire }
                        ]
                    }
                }
            }
            """);

        AssertEnumDeclared(document: document);
        Assert.Equal(
            expected: "Element",
            actual: Row(document: document, name: "recipe")["enum"]?.ToString()
        );
    }
    [Fact]
    public void EachDeclarationSpellsItsEnumAsARecordFieldDoes() {
        var document = WorldSources.LowerClean(body: $$"""
            state {
                {{Element}}
                world {
                    slot left: Element = Air
                    table recipe: Element {
                        a = Fire
                    }
                    grid field: Element dimensions(width: 2, depth: 2)
                }
            }
            """);

        AssertEnumDeclared(document: document);
        foreach (var name in ((string[])["left", "recipe", "field"])) {
            var row = Row(document: document, name: name);

            Assert.Equal(expected: "Element", actual: row["enum"]?.ToString());
            Assert.Equal(expected: "Int", actual: row["kind"]?.ToString());
        }
        Assert.Equal(
            expected: 1L,
            actual: Row(document: document, name: "left")["value"]?.GetValue<long>()
        );

        var printed = WorldSources.AssertRoundTrips(original: document);

        // A value an enum-drawn row holds decompiles as the member it names.
        Assert.Contains(actualString: printed, expectedSubstring: "slot left: Element = Air");
        Assert.Contains(actualString: printed, expectedSubstring: "a = Fire");
        Assert.Contains(actualString: printed, expectedSubstring: "table recipe: Element {");
        Assert.Contains(actualString: printed, expectedSubstring: "grid field: Element dimensions(");
    }
    // A record's enum field decompiles its default and every seed value as the member it names; a member two enums
    // share is qualified, since bare it names no one ordinal.
    [Fact]
    public void ARecordValueDecompilesAsTheMemberItNames() {
        var document = WorldSources.LowerClean(body: """
            state {
                enum Suit {
                    Clubs
                    Hearts
                    Spades
                }
                enum Tone {
                    Hearts
                    Bells
                }
                record Card {
                    suit: Suit = Spades
                    tone: Tone = Bells
                }
                pool cards of Card capacity(2) = [
                    { suit: Clubs, tone: Tone.Hearts }
                ]
            }
            """);
        var printed = WorldSources.AssertRoundTrips(original: document);

        Assert.Contains(actualString: printed, expectedSubstring: "suit: Suit = Spades");
        Assert.Contains(actualString: printed, expectedSubstring: "tone: Tone = Bells");
        Assert.Contains(actualString: printed, expectedSubstring: "suit: Clubs");
        Assert.Contains(actualString: printed, expectedSubstring: "tone: Tone.Hearts");
    }
    [Fact]
    public void AnEnumAnnotationSurvivesTheFormatter() {
        var document = WorldSources.ParseClean(body: $$"""
            state {
                {{Element}}
                world {
                    slot left: Element = Air
                    table recipe: Element {
                        a = Fire
                    }
                    grid field: Element dimensions(width: 2, depth: 2)
                }
            }
            """);
        var printed = PuckPrinter.Print(document: document);

        Assert.Contains(actualString: printed, expectedSubstring: "slot left: Element = Air");
        Assert.Contains(actualString: printed, expectedSubstring: "table recipe: Element {");
        Assert.Contains(actualString: printed, expectedSubstring: "grid field: Element dimensions(");
    }
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void AnUndeclaredEnumIsRefusedOnceAtTheLineThatNamesIt(string name) {
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: (WorldSources.Header + Refusals[name].Body),
            sourceMap: sourceMap
        );

        if (!compilation.Diagnostics.HasErrors) {
            _ = WorldSemanticValidator.ValidateWorld(
                diagnostics: compilation.Diagnostics,
                loweredJson: compilation.RequireJson(),
                sourceMap: sourceMap
            );
        }

        WorldSources.AssertRefusedBy(
            diagnostics: compilation.Diagnostics,
            label: name,
            refusal: Refusals[name]
        );
    }
    // A row may name an enum its basis carries into `state.enums`: the lowering cannot see the basis, and the composed
    // document the validation reads declares it.
    [Fact]
    public void ARowMayNameAnEnumItsBasisDeclares() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-enum-basis-{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: directory);

        try {
            File.WriteAllText(
                contents: $"{WorldSources.Header}state {{\n    {Element}    world {{\n        slot a: Element = Air\n    }}\n}}\n",
                path: Path.Combine(path1: directory, path2: "base.puck")
            );

            var path = Path.Combine(path1: directory, path2: "child.puck");
            var source = "basis: \"base\"\n\nstate {\n    world {\n        slot left: Element = 1\n    }\n}\n";
            var sourceMap = new SourceMap();

            File.WriteAllText(contents: source, path: path);

            var compilation = WorldCompiler.Compile(
                cancellationToken: TestContext.Current.CancellationToken,
                source: source,
                sourceMap: sourceMap,
                sourcePath: path
            );

            Assert.False(
                condition: compilation.Diagnostics.HasErrors,
                userMessage: compilation.Diagnostics.FormatReport(source)
            );
            Assert.True(
                condition: WorldSemanticValidator.ValidateComposedWorld(
                    diagnostics: compilation.Diagnostics,
                    loweredJson: compilation.RequireJson(),
                    sourceMap: sourceMap,
                    sourcePath: path
                ),
                userMessage: compilation.Diagnostics.FormatReport(source)
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
