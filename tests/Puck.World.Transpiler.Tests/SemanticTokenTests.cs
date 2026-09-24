using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Holds <see cref="PuckSemanticTokens"/> to a well-formed token stream over every shipped sample, to the roles
/// the VS Code grammar distinguishes, and holds the language server to advertising and serving them.</summary>
public class SemanticTokenTests {
    private static string SamplesDirectory() => RepositoryPaths.Resolve(relativePath: "src/Puck.World.Transpiler/Samples");
    private static PuckSemanticToken? TokenAt(IReadOnlyList<PuckSemanticToken> tokens, int offset) {
        foreach (var token in tokens) {
            if ((token.Offset <= offset) && (offset < (token.Offset + token.Length))) {
                return token;
            }
        }

        return null;
    }

    public static TheoryData<string> Samples() => [.. Directory.EnumerateFiles(
        path: SamplesDirectory(),
        searchOption: SearchOption.AllDirectories,
        searchPattern: "*.puck"
    ).Select(selector: path => Path.GetRelativePath(
        path: path,
        relativeTo: SamplesDirectory()
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    )).Order(comparer: StringComparer.Ordinal)];
    [MemberData(nameof(Samples))]
    [Theory]
    public void EveryTokenOfASampleLiesInsideItsTextAndNoTwoOverlap(string sample) {
        var source = File.ReadAllText(path: Path.Combine(
            path1: SamplesDirectory(),
            path2: sample
        ));
        var tokens = PuckSemanticTokens.Classify(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );
        var end = 0;

        Assert.NotEmpty(collection: tokens);

        foreach (var token in tokens) {
            Assert.True(
                condition: ((token.Length > 0) && (token.Offset >= end) && ((token.Offset + token.Length) <= source.Length)),
                userMessage: $"{sample}: token {token} overlaps the previous token (which ended at {end}) or leaves the text ({source.Length} characters)."
            );
            end = (token.Offset + token.Length);
        }

        var data = PuckSemanticTokens.Encode(
            source: source,
            tokens: tokens
        );
        var lines = source.Split(separator: '\n');
        var line = 0;
        var character = 0;

        Assert.Equal(
            actual: (data.Count % 5),
            expected: 0
        );

        for (var index = 0; (index < data.Count); index += 5) {
            line += data[index];
            character = ((data[index] == 0)
                ? (character + data[(index + 1)])
                : data[(index + 1)]
            );

            Assert.True(
                condition: ((line < lines.Length) && (data[(index + 2)] > 0) && ((character + data[(index + 2)]) <= lines[line].TrimEnd(trimChar: '\r').Length)),
                userMessage: $"{sample}: encoded token {(index / 5)} at {(line + 1)}:{(character + 1)} (length {data[(index + 2)]}) leaves its line."
            );
            Assert.InRange(
                actual: data[(index + 3)],
                high: (PuckSemanticTokens.TokenTypes.Count - 1),
                low: 0
            );
        }
    }
    [InlineData("palette [ { color: \"#888778\", roughness: 0.93 } ]", "palette", PuckSemanticRole.Property)]
    [InlineData("palette [ { color: \"#888778\", roughness: 0.93 } ]", "color", PuckSemanticRole.Property)]
    [InlineData("palette [ { color: \"#888778\", roughness: 0.93 } ]", "#888778", PuckSemanticRole.String)]
    [InlineData("palette [ { color: \"#888778\", roughness: 0.93 } ]", "0.93", PuckSemanticRole.Number)]
    [InlineData("noise { frequency: 1.8 }", "noise", PuckSemanticRole.Property)]
    [InlineData("noise { frequency: 1.8 }", "frequency", PuckSemanticRole.Property)]
    [InlineData("value: items[0]", "items", PuckSemanticRole.Variable)]
    [InlineData("value: items [0]", "items", PuckSemanticRole.Variable)]
    [InlineData("label: \"noise { [] }\"", "noise", PuckSemanticRole.String)]
    [InlineData("shape Superellipsoid \"stone\" {", "shape", PuckSemanticRole.Keyword)]
    [InlineData("shape Superellipsoid \"stone\" {", "Superellipsoid", PuckSemanticRole.Type)]
    [InlineData("blend: SmoothUnion", "SmoothUnion", PuckSemanticRole.EnumMember)]
    [InlineData("enabled: true", "true", PuckSemanticRole.EnumMember)]
    [InlineData("for (tile, tileIndex) in meadowTiles {", "for", PuckSemanticRole.Keyword)]
    [InlineData("for (tile, tileIndex) in meadowTiles {", "in ", PuckSemanticRole.Keyword)]
    [InlineData("for (tile, tileIndex) in meadowTiles {", "meadowTiles", PuckSemanticRole.Variable)]
    [InlineData("for tile in meadowTiles {", "tile ", PuckSemanticRole.Variable)]
    [InlineData("x: filter(meadowBlades, b => b[\"x\"] > 0)", "filter", PuckSemanticRole.Function)]
    [InlineData("x: filter(meadowBlades, b => b[\"x\"] > 0)", "=>", PuckSemanticRole.Operator)]
    [InlineData("x: filter(meadowBlades, b => b[\"x\"] > 0)", "(", PuckSemanticRole.Operator)]
    [InlineData("x: filter(meadowBlades, b => b[\"x\"] > 0)", "\"x\"", PuckSemanticRole.String)]
    [InlineData("prototype $\"meadow-{tileIndex}\" {", "prototype", PuckSemanticRole.Keyword)]
    [InlineData("prototype $\"meadow-{tileIndex}\" {", "meadow-", PuckSemanticRole.String)]
    [InlineData("prototype $\"meadow-{tileIndex}\" {", "{tileIndex", PuckSemanticRole.Keyword)]
    [InlineData("prototype $\"meadow-{tileIndex}\" {", "tileIndex", PuckSemanticRole.Variable)]
    [InlineData("shape Prism $\"\"\"stem-{blade[\"id\"]}\"\"\" {", "blade", PuckSemanticRole.Variable)]
    [InlineData("shape Prism $\"\"\"stem-{blade[\"id\"]}\"\"\" {", "\"id\"", PuckSemanticRole.String)]
    [InlineData("name: $\"{{literal}}-{tileIndex}\"", "{{", PuckSemanticRole.Keyword)]
    [InlineData("name: $\"{{literal}}-{tileIndex}\"", "literal", PuckSemanticRole.String)]
    [InlineData("name: \"\"\"literal { blade[\"id\"] }\"\"\"", "blade", PuckSemanticRole.String)]
    [InlineData("name: \"tab\\there\"", "\\t", PuckSemanticRole.Keyword)]
    [InlineData("// note [ {", "note", PuckSemanticRole.Comment)]
    [InlineData("/* a\nb */ x: 1", "b */", PuckSemanticRole.Comment)]
    [InlineData("duration: 250ms", "250", PuckSemanticRole.Number)]
    [InlineData("duration: 250ms", "ms", PuckSemanticRole.Keyword)]
    [InlineData("color: #1b4d3e", "#1b4d3e", PuckSemanticRole.Number)]
    [InlineData("let tickRate = 0.25s", "let", PuckSemanticRole.Keyword)]
    [InlineData("let tickRate = 0.25s", "tickRate", PuckSemanticRole.Variable)]
    [InlineData("rule \"score\" {\n  when vitals.hp > 0\n}", "rule", PuckSemanticRole.Keyword)]
    [InlineData("rule \"score\" {\n  when vitals.hp > 0\n}", "when", PuckSemanticRole.Keyword)]
    [InlineData("rule \"score\" {\n  when vitals.hp > 0\n}", "vitals", PuckSemanticRole.Variable)]
    [InlineData("rule \"score\" {\n  when vitals.hp > 0\n}", "hp", PuckSemanticRole.Property)]
    [InlineData("template pad(width = 2) {", "pad", PuckSemanticRole.Function)]
    [InlineData("schema: \"puck.world.definition.v1\"", "schema", PuckSemanticRole.Property)]
    [InlineData("a: `seat-1`", "`seat-1`", PuckSemanticRole.Variable)]
    [InlineData("range: 0..4", "..", PuckSemanticRole.Operator)]
    [InlineData("slot left = Air", "left", PuckSemanticRole.Variable)]
    [InlineData("slot left: Element = Air", "left", PuckSemanticRole.Variable)]
    [InlineData("table recipe: Element {\n  a = Air\n}", "recipe", PuckSemanticRole.Variable)]
    [InlineData("grid field: Element dimensions(width: 2, depth: 2)", "field", PuckSemanticRole.Variable)]
    [InlineData("slot left: Element = Air", "Element", PuckSemanticRole.EnumMember)]
    [InlineData("table recipe: Element {\n  a = Air\n}", "Element", PuckSemanticRole.EnumMember)]
    [InlineData("grid field: Element dimensions(width: 2, depth: 2)", "Element", PuckSemanticRole.EnumMember)]
    [InlineData("record Card {\n  suit: Suit\n}", "Suit", PuckSemanticRole.EnumMember)]
    [InlineData("row {\n  enum: Element\n}", "Element", PuckSemanticRole.EnumMember)]
    [InlineData("watchMemory screen: 0, address: 0, length: 4", "screen", PuckSemanticRole.Property)]
    [Theory]
    public void AWordTakesTheRoleItsPositionGivesIt(string source, string needle, PuckSemanticRole expected) {
        var offset = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: needle
        );

        Assert.True(
            condition: (offset >= 0),
            userMessage: $"'{needle}' is not in the source."
        );

        var token = TokenAt(
            offset: offset,
            tokens: PuckSemanticTokens.Classify(
                source: source,
                vocabulary: WorldDocumentVocabulary.Instance
            )
        );

        Assert.NotNull(@object: token);
        Assert.Equal(
            actual: token.Value.Role,
            expected: expected
        );
    }
    public static TheoryData<string> Operators() => new(values: [.. ExpressionOperators.Infix.Select(selector: static row => row.Symbol!), "~"]);
    // An operator is one token however many characters spell it, so `<<` is never two `<`.
    [MemberData(nameof(Operators))]
    [Theory]
    public void AnOperatorIsOneTokenSpanningItsWholeSymbol(string symbol) {
        var source = ((symbol == "~")
            ? "let mask = ~3"
            : $"let mask = 6 {symbol} 3"
        );
        var offset = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: symbol
        );
        var token = TokenAt(
            offset: offset,
            tokens: PuckSemanticTokens.Classify(source: source)
        );

        Assert.Equal(
            actual: token,
            expected: new PuckSemanticToken(
                Length: symbol.Length,
                Offset: offset,
                Role: PuckSemanticRole.Operator
            )
        );
    }
    [InlineData("let tickRate = 0.25s", "tickRate")]
    [InlineData("for tile in meadowTiles {", "tile ")]
    [InlineData("template pad(width = 2) {", "pad")]
    [Theory]
    public void ADeclaredNameCarriesTheDeclarationModifier(string source, string needle) {
        var token = TokenAt(
            offset: source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: needle
            ),
            tokens: PuckSemanticTokens.Classify(source: source)
        );

        Assert.NotNull(@object: token);
        Assert.Equal(
            actual: token.Value.Modifiers,
            expected: PuckSemanticModifiers.Declaration
        );
    }
    [Fact]
    public void BracketsBracesAndSeparatorsCarryNoToken() {
        const string Source = "palette [ { a: 1, b: 2 } ]; x: y.z";
        var tokens = PuckSemanticTokens.Classify(source: Source);

        for (var index = 0; (index < Source.Length); ++index) {
            if (Source[index] is '[' or ']' or '{' or '}' or ',' or ';' or '.') {
                Assert.Null(@object: TokenAt(
                    offset: index,
                    tokens: tokens
                ));
            }
        }
    }
    [Fact]
    public void AnEmbeddedLanguageBodyIsLeftToItsOwnGrammar() {
        const string Source = "sql \"setup\" {\n  CREATE TABLE t (a INT);\n}\nx: 1";
        var tokens = PuckSemanticTokens.Classify(
            source: Source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.Equal(
            actual: TokenAt(offset: 0, tokens: tokens)?.Role,
            expected: PuckSemanticRole.Keyword
        );
        Assert.Null(@object: TokenAt(
            offset: Source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: "CREATE"
            ),
            tokens: tokens
        ));
        Assert.Equal(
            actual: TokenAt(
                offset: Source.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    value: "x:"
                ),
                tokens: tokens
            )?.Role,
            expected: PuckSemanticRole.Property
        );
    }
    [Fact]
    public void ATokenCrossingALineBreakIsEncodedOnePiecePerLine() {
        const string Source = "a: \"\"\"one\r\ntwo\"\"\"";
        var data = PuckSemanticTokens.Encode(
            source: Source,
            tokens: PuckSemanticTokens.Classify(source: Source)
        );

        // property `a`, operator `:`, then the raw string's two pieces: `"""one` (without its CR) and `two"""`.
        Assert.Equal(
            actual: data,
            expected: [0, 0, 1, 0, 0, 0, 1, 1, 8, 0, 0, 2, 6, 5, 0, 1, 0, 6, 5, 0]
        );
    }
    [Fact]
    public async Task TheServerAdvertisesItsLegendAndServesTokensForAnOpenDocument() {
        const string Source = "schema: \"puck.world.definition.v1\"\nlet size = 2m\n";
        var transcript = await LanguageServerClient.SessionAsync(
            requests: [LanguageServerClient.Request(
                id: 2,
                method: "textDocument/semanticTokens/full",
                @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = LanguageServerClient.DefaultUri } }
            )],
            text: Source
        );
        var legend = transcript.Result(id: 1)?["capabilities"]?["semanticTokensProvider"]?["legend"];

        Assert.Equal(
            actual: Assert.IsType<JsonArray>(@object: legend?["tokenTypes"]).Select(selector: static node => node?.ToString()),
            expected: PuckSemanticTokens.TokenTypes
        );
        Assert.Equal(
            actual: Assert.IsType<JsonArray>(@object: legend?["tokenModifiers"]).Select(selector: static node => node?.ToString()),
            expected: PuckSemanticTokens.TokenModifiers
        );
        Assert.Equal(
            actual: Assert.IsType<JsonArray>(@object: transcript.Result(id: 2)?["data"]).Select(selector: static node => node!.GetValue<int>()),
            expected: PuckSemanticTokens.Encode(
                source: Source,
                tokens: PuckSemanticTokens.Classify(
                    source: Source,
                    vocabulary: WorldDocumentVocabulary.Instance
                )
            )
        );
    }
    // The VS Code extension colours each semantic token type as the TextMate scope its grammar gives the same role,
    // so a theme colours a word the same way whichever of the two reached it first.
    [Fact]
    public void TheEditorExtensionMapsEveryTokenTypeToAGrammarScope() {
        var manifest = JsonNode.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "editors/vscode/package.json")));
        var scopes = (Assert.IsType<JsonArray>(@object: manifest?["contributes"]?["semanticTokenScopes"])
            .Single(predicate: static entry => (entry?["language"]?.ToString() == "puck"))?["scopes"] as JsonObject);

        Assert.NotNull(@object: scopes);

        foreach (var type in PuckSemanticTokens.TokenTypes) {
            Assert.True(
                condition: (scopes.TryGetPropertyValue(jsonNode: out var mapped, propertyName: type) && (mapped is JsonArray { Count: > 0 })),
                userMessage: $"editors/vscode/package.json maps no grammar scope for the semantic token type '{type}'."
            );
        }
    }
}
