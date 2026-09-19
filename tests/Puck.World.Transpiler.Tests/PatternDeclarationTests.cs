using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>pattern</c> declaration: its alphabet block, its match algebra, the row it lowers to, and the
/// source text it decompiles back to. A pattern whose derivative machine outgrows its budget is refused where it is
/// written rather than where the document is validated.</summary>
public class PatternDeclarationTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }
    private static JsonObject LowerClean(string body) {
        var (json, diagnostics) = Lower(body: body);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );

        return json;
    }
    private static JsonObject FirstPattern(JsonObject json) =>
        Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["patterns"])[0]);

    private const string RunPattern = """
        pattern hand : Int {
          value: "face[$token]"
          symbols {
            face = 1..2
            next = 2
          }
          match: face next*
        }
        """;

    [Fact]
    public void TheDeclarationParsesItsAlphabetAndItsLanguage() {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{RunPattern}";

        var (document, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(source)
        );

        var declaration = Assert.IsType<PatternDeclarationNode>(@object: document!.Statements[0]);

        Assert.Equal(
            "hand",
            declaration.Name
        );
        Assert.Equal(
            "Int",
            declaration.Kind
        );
        Assert.Equal(
            [("face", 1m, 2m), ("next", 2m, 2m)],
            declaration.Symbols.Select(selector: symbol => (symbol.Name, symbol.Minimum, symbol.Maximum))
        );

        var sequence = Assert.IsType<PatternNode.Sequence>(@object: declaration.Match);

        Assert.Equal(
            "face",
            Assert.IsType<PatternNode.Symbol>(@object: sequence.Items[0]).Name
        );
        Assert.Equal(
            "next",
            Assert.IsType<PatternNode.Symbol>(@object: Assert.IsType<PatternNode.Star>(@object: sequence.Items[1]).Item).Name
        );
    }
    [Fact]
    public void TheDeclarationLowersToOnePatternsRow() {
        var row = FirstPattern(json: LowerClean(body: RunPattern));

        Assert.Equal(
            "hand",
            row["name"]!.ToString()
        );
        Assert.Equal(
            "Int",
            row["kind"]!.ToString()
        );
        Assert.Equal(
            "sequence",
            row["pattern"]!["$type"]!.ToString()
        );
        Assert.Equal(
            "star",
            Assert.IsType<JsonArray>(@object: row["pattern"]!["items"])[1]!["$type"]!.ToString()
        );

        var symbols = Assert.IsType<JsonArray>(@object: row["symbols"]);

        Assert.Equal(
            "face",
            symbols[0]!["name"]!.ToString()
        );
        Assert.Equal(
            2,
            symbols[0]!["max"]!.GetValue<decimal>()
        );
        Assert.NotNull(@object: row["value"]!["instructions"]);
    }
    [Fact]
    public void TheLoweredRowReadsBackThroughTheDocumentsOwnSerializer() {
        var document = LowerClean(body: RunPattern);
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: document.ToJsonString()));
        var row = Assert.Single(collection: definition.Patterns);

        Assert.Equal(
            "hand",
            row.Name.Value
        );
        Assert.Equal(
            CellKind.Int,
            row.Kind
        );

        var sequence = Assert.IsType<PatternNode.Sequence>(@object: row.Pattern);

        Assert.Equal(
            2,
            sequence.Items.Count
        );
    }
    // A row whose name the parser would not read as an identifier, or whose kind is not spelled as its enum member,
    // stays as plain document text, so whatever the decompiler prints compiles back to the same document.
    [Theory]
    [InlineData("name", "probe-name")]
    [InlineData("name", "probe name")]
    [InlineData("name", "9probe")]
    [InlineData("kind", "int")]
    public void ARowTheDeclarationCannotSpellIsNotSugared(string member, string value) {
        var document = LowerClean(body: RunPattern);

        FirstPattern(json: document)[member] = value;

        var source = WorldDecompiler.Decompile(root: document);

        Assert.DoesNotContain(
            actualString: source,
            expectedSubstring: "pattern "
        );

        var again = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(
            condition: again.Diagnostics.HasErrors,
            userMessage: again.Diagnostics.FormatReport(source)
        );
    }
    [Fact]
    public void ARowNamedWithADollarStillDecompilesAsADeclaration() {
        var document = LowerClean(body: RunPattern);

        FirstPattern(json: document)["name"] = "probe$name";

        Assert.Contains(
            actualString: WorldDecompiler.Decompile(root: document),
            expectedSubstring: "pattern probe$name : Int {"
        );
    }
    [Fact]
    public void TheDeclarationDecompilesBackToTheSameSourceText() {
        var document = LowerClean(body: RunPattern);
        var source = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: source,
            expectedSubstring: "pattern hand : Int {"
        );
        Assert.Contains(
            actualString: source,
            expectedSubstring: "match: face next*"
        );
        Assert.Contains(
            actualString: source,
            expectedSubstring: "face = 1..2"
        );
        Assert.Contains(
            actualString: source,
            expectedSubstring: "next = 2"
        );

        var relowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: relowered.Diagnostics.HasErrors,
            userMessage: relowered.Diagnostics.FormatReport(source)
        );
        Assert.Equal(
            document.ToJsonString(),
            relowered.RequireJson().ToJsonString()
        );
    }
    [Fact]
    public void TheFormatterLeavesARepetitionCountWhereItStands() {
        var source = """
            schema: "puck.world.definition.v1"

            pattern wide : Int {
              symbols {
                a = 1
              }
              match: a{2, 4} a{3}
            }

            """;
        var formatted = PuckFormat.Format(source: source);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "match: a{2, 4} a{3}"
        );
        Assert.Equal(
            formatted,
            PuckFormat.Format(source: formatted)
        );
    }
    [Fact]
    public void AMachineLargerThanItsBudgetIsRefusedByName() {
        var (_, diagnostics) = Lower(body: """
            pattern wide : Int {
              maxStates: 2
              symbols {
                a = 1
                b = 2
              }
              match: a{6,6} b
            }
            """);

        var refusal = Assert.Single(
            collection: diagnostics,
            predicate: d => (d.Code == "PUCK098")
        );

        Assert.Contains(
            expectedSubstring: "past the 2 it budgets",
            actualString: refusal.Message
        );
    }
    [Fact]
    public void TheSameMachineInsideItsBudgetIsAdmitted() {
        var (_, diagnostics) = Lower(body: """
            pattern wide : Int {
              maxStates: 64
              symbols {
                a = 1
                b = 2
              }
              match: a{6,6} b
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );
    }
    [Fact]
    public void AMatchNamingNoDeclaredSymbolIsRefusedByName() {
        var (_, diagnostics) = Lower(body: """
            pattern hand : Int {
              symbols {
                face = 1
              }
              match: face ghost
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK097")
        );
    }
    [Fact]
    public void AMatchTheAlgebraCannotReadIsRefusedWhereItIsWritten() {
        var source = """
            schema: "puck.world.definition.v1"

            pattern hand : Int {
              symbols {
                face = 1
              }
              match: face |
            }
            """;

        var (_, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK097")
        );
    }
}
