using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP coverage for the `.puck` DSL sugar wave: completion for the new keywords, hover on a declared state
/// row's kind, and documentSymbol entries for `rule` blocks.</summary>
public class LspSugarTests {
    private const string SourceWithRuleAndStateRow = "puck: 1\nstate {\n    world: [\n        {\n            name: \"hp\"\n            kind: Int\n            capacity: 1\n        }\n    ]\n}\nrule \"heal\" {\n    when hp < 10\n    hp += 1\n}\n";

    [Fact]
    public async Task CompletionOffersTheNewGateAndEffectKeywords() {
        var labels = await LanguageServerClient.CompletionLabelsAsync(markedSource: $"|{SourceWithRuleAndStateRow}");

        foreach (var keyword in new[] { "when", "and", "or", "not", "local", "push", "remove", "schedule", "transform", "transaction", "onFailure", "decision", "option", "interrupt", "onNoChoice", "rule", "shape", "placements", "placement" }) {
            Assert.Contains(
                expected: keyword,
                set: labels
            );
        }
    }
    [Fact]
    public async Task DocumentSymbolReportsARuleBlockWithItsGateAsAChild() {
        var symbols = Assert.IsType<JsonArray>(@object: await LanguageServerClient.DocumentRequestAsync(
            method: "textDocument/documentSymbol",
            text: SourceWithRuleAndStateRow
        ));
        var ruleSymbol = symbols.FirstOrDefault(predicate: s => (s?["name"]?.ToString() == "rule \"heal\""));

        Assert.NotNull(@object: ruleSymbol);
        var children = ruleSymbol!["children"]?.AsArray();

        Assert.NotNull(@object: children);
        Assert.Contains(
            collection: children,
            filter: child => (child?["name"]?.ToString() == "when")
        );
    }
    // The editor receives exactly the text `puck format` writes, whatever indentation the client asks for.
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    [Theory]
    public async Task FormattingIgnoresClientIndentation(int tabSize, bool insertSpaces) {
        var edits = await LanguageServerClient.DocumentRequestAsync(
            method: "textDocument/formatting",
            options: new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces },
            text: "host { width: 1280, height: 720 }"
        );

        Assert.Equal(
            PuckFormat.Format(source: "host { width: 1280, height: 720 }"),
            edits?[0]?["newText"]?.ToString()
        );
    }
    [InlineData("let seats = 4\ncount: foreign.sea|ts\n")]
    [InlineData("let seats = 4\n// sea|ts\n")]
    [InlineData("let seats = 4\n/* sea|ts */\n")]
    [InlineData("let seats = 4\nlabel: \"sea|ts\"\n")]
    [InlineData("let seats = 4\ncount: seats| + 1\n")]
    [InlineData("template tile(size = 2) { width: size }\nwidth: si|ze\n")]
    [InlineData("rule \"x\" {\nlocal amount = 2\nhp += amount\n}\nrule \"y\" { hp += amo|unt }\n")]
    [InlineData("let data = { exp|onent: 2.7 }\n")]
    [InlineData("schema: \"puck.cartridge.v1\"\nshape Box \"x\" { exp|onent: 2.7 }\n")]
    [InlineData("schema: \"puck.creation.v1\"\nnoise { rough|ness: 0.5 }\n")]
    [Theory]
    public async Task HoverDoesNotInventOutOfScopeOrNonCodeSymbols(string source) {
        Assert.Null(@object: await LanguageServerClient.RequestAtAsync(
            cursor: MarkedSource.Parse(marked: source),
            method: "textDocument/hover"
        ));
    }
    [Fact]
    public async Task HoverRecognizesAnInlineAuthoredCompositionMember() {
        var card = await LanguageServerClient.HoverAsync(markedSource: "ground floor { si|ze [8m, 6m] }");

        Assert.NotNull(@object: card);
        Assert.Contains(actualString: card, comparisonType: StringComparison.Ordinal, expectedSubstring: "A `ground` member");
        Assert.Contains(actualString: card, comparisonType: StringComparison.Ordinal, expectedSubstring: "positive width and depth");
    }
    [InlineData("na|me: \"weathered-limestone\"", "name", "name")]
    [InlineData("palette [{ co|lor: \"#888778\" }]", "palette.color", "base color")]
    [InlineData("palette [{ rough|ness: 0.93 }]", "palette.roughness", "GGX roughness")]
    [InlineData("palette [{ spec|ular: 0.06 }]", "palette.specular", "specular strength")]
    [InlineData("noise { freq|uency: 1.8 }", "noise.frequency", "frequency")]
    [InlineData("noise { amp|litude: 0.04 }", "noise.amplitude", "amplitude")]
    [InlineData("noise { oct|aves: 2 }", "noise.octaves", "octave")]
    [InlineData("noise { ga|in: 0.4 }", "noise.gain", "gain")]
    [InlineData("noise { se|ed: 7341 }", "noise.seed", "seed")]
    [InlineData("shape Superellipsoid \"stone\" { pos|ition [0, 0.43, 0] }", "shape.position", "position")]
    [InlineData("shape Superellipsoid \"stone\" { sc|ale [0.95, 0.75, 0.7] }", "shape.scale", "per-axis scale")]
    [InlineData("shape Superellipsoid \"stone\" { exp|onent: 2.7 }", "shape.exponent", "exponent")]
    [InlineData("shape Superellipsoid \"stone\" { rot|ation [0, 0, 0, 1] }", "shape.rotation", "orientation")]
    [InlineData("shape Superellipsoid \"stone\" { mat|erial: 0 }", "shape.material", "palette slot")]
    [InlineData("shape Superellipsoid \"stone\" { bl|end: SmoothUnion }", "shape.blend", "blend op")]
    [InlineData("shape Superellipsoid \"stone\" { smo|oth: 0.16 }", "shape.smooth", "smooth-blend radius")]
    [InlineData("shape Super|ellipsoid \"stone\" { exponent: 2.7 }", "shape.type: Superellipsoid", "Superellipsoid")]
    [InlineData("shape Superellipsoid \"stone\" { blend: Smooth|Union }", "blend: SmoothUnion", "SmoothUnion")]
    [Theory]
    public async Task HoverExplainsCreationFieldsInsideAWorldPrototype(string body, string field, string description) {
        var source = (("schema: \"puck.world.definition.v1\"\nprototypes { prototype \"limestone\" { document {\nschema: \"puck.creation.v1\"\n" + body) + "\n} } }\n");
        var text = await LanguageServerClient.HoverAsync(markedSource: source);

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: field
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: description
        );
    }
    [Fact]
    public async Task HoverOnADeclaredStateRowReportsItsKind() {
        // "hp" sits at line 12 ("    hp += 1"), 0-based.
        var hoverText = LanguageServerClient.HoverText(result: await LanguageServerClient.RequestAtAsync(
            cursor: new MarkedSource(
                Character: 5,
                Line: 12,
                Text: SourceWithRuleAndStateRow
            ),
            method: "textDocument/hover"
        ));

        Assert.NotNull(@object: hoverText);
        Assert.Contains(
            actualString: hoverText,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "hp"
        );
        Assert.Contains(
            actualString: hoverText,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Int"
        );
    }
    [InlineData("// Number of seats.\nlet seats = 4\ncount: sea|ts\n", "let seats = 4", "Number of seats.")]
    [InlineData("count: sea|ts\nlet seats = 4\n", "let seats = 4", "compile-time constant")]
    [InlineData("template tile(size = 2) { width: size }\nt|ile()\n", "template tile(size = 2)", "template")]
    [InlineData("let size = 99\ntemplate tile(size = 2) { width: si|ze }\n", "size = 2", "template parameter")]
    [InlineData("template tile(size = 2) { width: size }\ntile(si|ze: 3)\n", "size = 2", "template parameter")]
    [InlineData("let item = 99\nvalues: map([1, 2], item => it|em + 1)\n", "item => item + 1", "lambda parameter")]
    [InlineData("let item = 99\nvalues: map([1, 2], item => item + 1)\ncount: it|em\n", "let item = 99", "compile-time constant")]
    [InlineData("for item in range(0, 2) { value: it|em }\n", "for item in range(0, 2)", "loop variable")]
    [InlineData("rule \"x\" {\nlocal amount = 2\nhp += amo|unt\n}\n", "local amount = 2", "rule local")]
    [InlineData("values: ra|nge(3, 5)\n", "range(start, count)", "count consecutive integers")]
    [InlineData("value: cla|mp(4, 0, 3)\n", "clamp(arg1, arg2, arg3)", "Arguments: 3")]
    [InlineData("let seats = 4\nbroken: [\ncount: sea|ts\n", "let seats = 4", "compile-time constant")]
    [Theory]
    public async Task HoverResolvesDeclarationsAndFunctions(string markedSource, string signature, string description) {
        var text = await LanguageServerClient.HoverAsync(markedSource: markedSource);

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: signature
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: description
        );
    }
    [InlineData("schema: \"puck.creation.v1\"\nshape Box \"x\" { mat|erial: 0 }", "palette slot")]
    [InlineData("host { wi|dth: 1280 }", "width in pixels")]
    [InlineData("value: orbit(ya|w: 0deg)", "heading in radians")]
    [InlineData("value: setState(st|ate: \"hp\", value: 3)", "state row name")]
    [Theory]
    public async Task HoverUsesOwningSchemaForFieldsAndCallArguments(string source, string expected) {
        var text = await LanguageServerClient.HoverAsync(markedSource: source);

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: expected
        );
    }
    [Fact]
    public void SchemaExportsCreationHelp() {
        var schema = WorldSchema.Export(postRenderExtensions: []);
        var descriptions = (string.Join(
            separator: "\n",
            values: schema.Sections.Select(selector: section => section.Node.ToJsonString())
        ) + schema.Common.ToJsonString());

        Assert.Contains(
            actualString: descriptions,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "GGX roughness"
        );
        Assert.Contains(
            actualString: descriptions,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "x-enumDescriptions"
        );
        var prototypes = schema.Sections.Single(predicate: section => (section.Name == "prototypes")).Node;
        var name = prototypes["items"]?["properties"]?["document"]?["properties"]?["name"];

        Assert.NotNull(@object: name?["description"]);
        // Hover annotations must not activate WorldJsonContext converter constraints in the creation serializer.
        Assert.Null(@object: name?["type"]);
    }
}
